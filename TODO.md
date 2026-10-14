# TODO

미완성 작업과 알려진 구조적 한계를 모아둔다. 완료된 것들의 상세한 발견-원인-해결 경위는
`KNOWN_ISSUES.md`에, 지금 구조가 왜 이렇게 됐는지는 `ARCHITECTURE.md`에 있다 - 여기는
"뭐가 남았는지"만 빠르게 훑는 용도로 간결하게 유지한다.

---

- **`AccountControlService.CreateAccount`에 닉네임 길이 제한이 없음** -
  클라 쪽(`AutoChessPlayerState.Nickname`)이 `FixedString32Bytes`(UTF8
  최대 29바이트)로 리플리케이트되기 때문에, 그보다 긴 닉네임으로 가입하면
  화면에서 잘려서 표시된다. 계정 생성 시점에 길이 검증(예: 29바이트
  이하)을 추가하는 게 맞음(`Unity/TODO.md`의 관련 항목 참고).

- **게임 결과(승패/점수) → 계정 동기화가 아예 미구현** -
  `GameAgent/Services/GameServerHandler.cs.HandleGameResultAsync`는 인게임
  서버가 보낸 `GameResult`(순위/승패/획득 점수)를 받고도 로비 복귀 처리만
  하고, 그 결과를 계정 서버에 반영하는 코드가 없다(기존 TODO 주석:
  "Kafka에 발행해 기록/보상 처리하는 부분은 미구현" - `result.Players`가
  지금 아무 데도 안 쓰임). 이 동기화 자체를 만들 때, 그 사이 계정이
  삭제된 경우(삭제 요청과 동기화 요청이 겹치는 레이스 - 계정 삭제
  관련 사고실험에서 나온 논의)를 계정 서버가 조용히 무시하도록
  처리해야 함 - 지금은 이 동기화도, 그 무시 처리도 둘 다 없음.

- **닉네임 파이프라인 - 전 구간 완료** (계정 서버 → 로그인 서버 → 인게임
  서버↔게임 에이전트 RPC → Unity 클라, 4단계 전부):
  - **계정 서버**: `account.proto` - `CreateAccountRequest.nickname` 필드
    (계정 생성 시에만 입력, 이후 변경 경로 없음) + `GetNicknames` RPC
    (`GetFriends`와 같은 패턴 - 배치 조회, 없는 playerId는 결과에서
    빠짐). `AccountRecord.Nickname`/`IAccountRepository.GetNicknamesAsync`·
    `NicknameExistsAsync`(중복 검사)/`AccountControlService.CreateAccount`
    (닉네임 필수 검증 + 중복 검사 + 저장, TOCTOU 레이스 방어는 안 함 -
    진짜 DB 생기면 UNIQUE 제약으로 대체)·`GetNicknames`/`AccountServerIntermediary.GetNicknamesAsync`
    (공용).
  - **로그인 서버**: `login.proto`의 `LoginRedirect.nickname`(최초 로그인
    응답에서만 채움 - 재진입 응답엔 안 채움). `LoginConnectionHandler.CompleteLoginAsync`가
    `CreateAccountRequest` 경로는 요청의 값을 그대로, `Credentials` 경로는
    신설한 `ResolveNicknameAsync`로 조회해서 채움. 곁다리 버그(`RequestLobbyAddressAsync`
    메서드 시그니처 선언 누락)도 같이 고침.
  - **인게임 서버 ↔ 게임 에이전트 RPC**: `ingame.proto`에 `GetNicknamesRequest`
    (`request_id`로 상관관계 추적 - 방 전체 단위라 `client_id` 불가)/
    `GetNicknamesResult`. `GameServerHandler.HandleGetNicknamesAsync`가
    `HandleFriendCommandAsync`와 같은 패턴으로 계정 서버 대리 호출.
  - **Unity**: `IIngameServerChannel`/`GrpcIngameServerChannel`에 송수신 +
    `IngameServerDispatchEvent`/`IngameServerMainThreadDispatcher`에 새
    케이스. `IngameServerInstance.RequestNicknamesAsync`(Guid 상관관계,
    `TaskCompletionSource`, 메인 스레드에서 풀림). `Room.Nicknames`(저장소) +
    `RoomManager.PromoteDrawingRoomAsync`(로스터 확정 시점에 사람 전원의
    닉네임을 미리 받아서 `Room`에 채워둠 - 봇은 계정이 없어 조회 대상에서
    제외). `ClientInstance.Nickname`(최초 로그인 성공 시에만 채워지고
    재접속으로는 절대 안 덮어씀). `AutoChessPlayerState.Nickname`(신설) -
    `AutoChessGameState.RegisterPlayerState`가 `Room.Nicknames`에서 받아
    채우고, `UpdatePlayerState`(체력 갱신용)는 기존 값을 그대로 보존(안
    그러면 데미지 입을 때마다 닉네임이 지워짐). `AutoChessPlayerInfoIndicator`가
    Team 대신 닉네임을 표시(봇처럼 빈 문자열이면 Team으로 폴백).




## 코드 리뷰로 발견한 버그 후보 (미검증)

코드를 읽다가 발견한 것들이라 아직 실제로 재현해보지 않았다 - 재현되면
`KNOWN_ISSUES.md` 형식(발견-원인-예상 피해-해결-결과)으로 옮기고, 재현이
안 되면 왜 안 되는지 적고 지울 것.

- **정상 종료한 게임만큼 게임 에이전트 부하(`CurrentLoad`)가 영구히 쌓임** -
  `GameAgent/Services/GameServerHandler.cs`의 `HandlePlayerJoinedAsync`가
  `CurrentLoad++`, `HandlePlayerLeftAsync`만 감소시킨다. 그런데 정상 종료
  (`CloseRoomAndRedirectAsync`)로 끊긴 클라는 Unity
  `IngameServerInstance.OnClientDisconnected`가 "이미 정리된 방"으로 보고
  `PlayerLeft`를 안 보내고, `CloseRoomAndRedirectAsync` 자신도 부하를 안 줄인다.
  Unity가 `SendStatusAsync`(실제 부하 보고)를 어디서도 호출하지 않아서 보정
  경로도 없다. 예상 피해: Capacity(기본 8)만큼 게임이 정상 종료되면
  `IngameMatchService.PrepareMatch`가 이후 모든 매치를 "수용 불가"로 거부.
  재현: 같은 게임 에이전트로 게임을 9판 연속 끝까지 진행.

- **매치 중단 후 대기열 복귀 시 `GameType` 유실 + 매칭 타임아웃이 영원히 안 걸림** -
  `MatchingServer/Services/MatchEngine.cs`의 `DisruptMatch`가 `WaitingPlayer`를
  다시 만들 때 `GameType`을 안 채워서 `GAME_TYPE_UNSPECIFIED`가 되고(`MatchPlayer`에
  애초에 GameType 필드가 없음), `JoinedAtUtc`를 매번 지금 시각으로 갱신해서
  `CheckTimeouts`가 절대 발동하지 않는다. 예상 피해: PrepareMatch가 거부되거나
  (위 부하 누적 항목과 연쇄) 방 생성이 실패하면 1초마다 재시도 → 실패 → 복귀를
  무한 반복, 클라는 `MatchFailed`도 못 받고 무한 대기. Unspecified로 방 생성을
  요청하면 Unity `RoomManager.OpenRoom`이 `UnsupportedGameType`으로 거부하므로
  한 번 중단된 플레이어는 매칭이 다시 성공할 수도 없다.

- **재접속 유예 타이머가 끊김마다 따로 돌아서, 이전 타이머가 세션을 일찍 지움** -
  `GameServerHandler.HandlePlayerLeftAsync`의 `Task.Run` 타이머는 30초 뒤
  "지금 방에 있는지"만 본다. 끊김(t=0) → 재접속(t=5) → 다시 끊김(t=25)이면
  t=30에 첫 타이머가 "방에 없음"으로 판단해 세션을 삭제한다 - 두 번째 끊김의
  유예(t=55까지)가 남아있는데도. 끊김마다 세대 번호(또는 CTS)를 두고 최신
  타이머만 유효하게 해야 함.
  - 같은 영역 - **유예 시간 불일치**: Unity 쪽 탈주 확정
    (`NetworkTimeouts.MidGameAbandonTimeout`)은 20초, 게임 에이전트
    (`ReconnectGracePeriod`)는 30초. 20~30초 사이에 재접속하면 게임 에이전트는
    승인하지만 Unity에선 이미 `Eliminate()`된 상태로 방에 들어온다. 한쪽 기준으로
    통일 필요.

- **전입 티켓 만료(10초) 후엔 티켓 검증 자체가 생략됨** -
  `Shared.Common/Account/MainServerIntermediary.cs`의 `TicketTtl`(10초)이 지나면
  티켓이 딕셔너리에서 지워지는데, 검증 쪽(`GameServerHandler.HandleApprovalRequestAsync`,
  `LobbyControlService.EnterLobby`)은 `HasPendingTicket`이 false면 "재접속이라 티켓
  불필요"로 보고 그냥 통과시킨다. 예상 피해: 훔친 토큰 방어가 10초 동안만 동작 -
  그 뒤에 도착하면 토큰만으로 통과. 또 정상 클라라도 접속이 10초 넘게 걸리면
  (NetworkManager 종료 대기 최대 2초 + 접속 지연) 보호 없이 들어오게 됨. "만료된
  티켓"과 "애초에 티켓이 없는 재접속"을 구분할 수 있게 해야 함.

- **(Unity 인게임 서버) 응접실 승격 도중의 접속/이탈 이벤트가 유실됨** -
  `Unity/.../Network/Server/Managers/RoomManager.cs`의 `PromoteDrawingRoomAsync`가
  `_drawingRooms`에서 응접실을 먼저 빼고 나서 `RequestNicknamesAsync`를 await한다.
  그 사이 도착한 `RegisterConnectedClient`/`UnregisterConnectedClient`는 `_rooms`에도
  `_drawingRooms`에도 방이 없어서 조용히 무시된다. 예상 피해: 그 틈에 접속한 클라는
  PlayerController 없이 남고, 그 틈에 끊긴 클라는 접속 중인 걸로 남아 팀/보드를
  배정받음. 닉네임 조회에 타임아웃이 없어서(게임 에이전트 무응답 시) 이 틈이
  무한정 길어질 수도 있음. `Unity/TODO.md`에도 같이 적어둘지 검토.




## 통합 테스트 체크리스트

시나리오(S1, S2...)로 묶어서 진행하던 걸 그만두고, 지금은 항목 하나씩 개별로
확인하는 방식으로 진행 중이다 - 그래서 아래도 시나리오 단위가 아니라 항목 단위
체크리스트로 유지한다. 완료된 항목은 무엇을 확인했고 어떤 버그를 찾았는지까지
같이 적어둔다(상세 경위는 `KNOWN_ISSUES.md`).

### 기본 플로우
- [x] 클라 로그인 성공 / 실패(틀린 비밀번호 → `INVALID_CREDENTIALS` 거부) 확인
- [x] 로그인 성공 후 로비 서버 진입 확인
- [x] 매칭 요청 → 매칭 성공 → 인게임 서버로 이동 확인
- [x] 인게임 서버에서 게임 진행(`DummyGameLogic` 자동 종료) 확인
- [x] 게임 종료 후 클라가 새 로비 서버로 정상 복귀하는지 확인
- [x] 마스터 서버 지시로 전체 서버가 정상 종료되는지 확인
- [x] 친구 추가 기능 확인
- [x] 채팅 기능(친구 간 메시지 송수신) 확인
- [x] 마스터 서버 지시로 로비 서버가 접속 중인 클라 전원에게 공지를 배포하는지,
      늦게 합류한 클라도 놓치지 않고 받는지 확인 - `GlobalWorker`의 배달 지연 버그
      발견/수정 (`KNOWN_ISSUES.md` #13)
- [x] `EnterLobby`를 300ms 안에 연속 호출하면 rate limit이 걸리는지 확인
- [x] 여러 클라가 동시에 매칭 요청 시 각자 독립적으로 매칭되는지 확인
      (매칭 엔진이 "더미: 1명씩 매칭"이라 같은 방으로 안 묶이는 게 정상)

### 장애 주입 (서버/프로세스 다운)
- [x] 로비 서버 강제 종료 - 마스터가 하트비트 타임아웃(15초)으로 감지해서
      `하트비트 타임아웃으로 접속 종료 처리: {InstanceId}` 로그와 함께 인스턴스
      레지스트리에서 정상적으로 제거되는지 확인
- [x] 매칭 서버 재시작 - 로비/게임 에이전트가 재연결하는지 확인
- [x] 게임 에이전트 강제 종료 - 실제 게임 서버가 15초 하트비트 타임아웃 후
      `GameManager.Shutdown()`이 `Room → Network` 순서대로 정리하고 `Application.Quit()`까지
      호출되는지 로그로 확인 (에디터 Play Mode에서는 `Application.Quit()`이 무효라
      실제 프로세스 종료 자체는 Standalone 빌드로 별도 확인 필요)
- [x] 매칭 진행 중(대기 등록 이후) 매칭 서버가 죽었을 때, 대기 중이던 클라가
      "매칭 실패" 통보를 받는지 - 버그 2건 발견/수정:
      1) `MatchNotificationDispatcher`에 매칭 서버 연결 끊김 감지 핸들러 자체가
         없어서 대기 중인 클라가 무한 대기했음 (`KNOWN_ISSUES.md` #15)
      2) `ClientInstance`가 `OnMatchFailed` 이벤트를 아예 구독 안 해서 서버가
         정상 통보해도 클라 쪽에서 조용히 사라졌음 (`KNOWN_ISSUES.md` #16)
- [x] Kafka/RabbitMQ/Redis 브로커가 죽어있는 상태에서 서버 기동/운영 - 크래시 없이
      견디는지. 버그 2건 발견/수정:
      1) Redis가 재시작되면 서버가 영영 "등록 안 된 서버"로 남는 문제
         (`KNOWN_ISSUES.md` #17)
      2) Kafka 다운 시 발행 요청이 기본값(5분)을 다 채우고서야 실패로 확정되던 문제
         (`KNOWN_ISSUES.md` #18)
      RabbitMQ는 다운/복구 둘 다 크래시 없이 견딤 확인(복구 후 형식이 안 맞는
      테스트 메시지가 안전하게 버려지는 것도 의도된 동작으로 확인).

### 재접속 시나리오
- [x] 인게임 도중 연결 끊김 → 유예 시간(30초) 내 재접속 성공 - 버그 5건 발견/수정
      후 최종 통과 확인 (`KNOWN_ISSUES.md` #19~#23)
- [x] 인게임 도중 연결 끊김 → 유예 시간 초과 → 세션 완전 삭제 (방이 정상적으로
      삭제되는 것까지 확인됨)
- [x] 로비에 있던 클라의 연결이 끊김 → 세션 삭제 + 채팅 서버 연결도 같이 끊기는지 -
      세션 삭제는 정상 확인, 채팅 강제 종료는 버그로 인해 무한 정지하던 걸 발견/
      수정 후 통과 (`KNOWN_ISSUES.md` #24)
- [x] 매치 확정 순간과 로비 연결 종료가 겹치는 타이밍 - 정상 이동 중인 클라 세션이
      실수로 삭제되지 않는지 확인됨 (인위적 지연으로 race 강제 재현, `KNOWN_ISSUES.md`
      #6 방어 로직이 실제로 동작하는 것을 회귀 테스트로 확인)
- [x] 다른 서버가 아직 세션을 물고 있는 상태에서 재로그인 시도 - `SESSION_CONFLICT`로
      정상 거부 확인. (처음엔 "거부 + 안내"라고 적었는데, `LoginRejected`는 이름 그대로
      단순 거부만 하는 게 맞는 설계이고 안내는 별도 재진입 경로(#19~#23)가 담당 -
      항목 문구 정정)
- [x] 로그인 서버 재진입 - 도착지 서버 사망/토큰 전부 만료 상태에서 재로그인. 로그인
      서버가 (아직 TTL 안 지난) 죽은 서버 주소로 정확히 재안내하는 것까지는 확인, 그
      뒤 클라의 접속 시도가 성공/실패 신호 없이 무한정 멈추던 버그 발견/수정
      (`KNOWN_ISSUES.md` #25) - 포기 이후 동작(재진입/UI/종료)은 TODO로 남김

### 보안 회귀
- [x] 남의 playerId를 추측해서 토큰으로 제출 - 거부되는지. `AuthInterceptor`는 토큰
      형식만 확인하고 실제 신원 대조(`TokenVerification`)는 각 핸들러 책임이라는 걸
      확인, `EnterLobby`가 위조 토큰(`아무값:훔친playerId`)을 `INVALID_TOKEN`으로
      정상 거부하는 것까지 확인됨 (`Unity/.../Dummy/SecurityRegressionChecks.cs`)
- [x] 훔친 영구 토큰으로 목적지 서버에 먼저 도착 시도 - 전입 티켓 없이는 거부되는지.
      전입 티켓 검증이 양방향(로비↔인게임) 모두 사실상 죽어있던 버그 발견/수정,
      재진입 주소 버그 1건 및 `PlayerStatus.Matching` 세션 리크도 같이 발견/제거
      (`KNOWN_ISSUES.md` #26~#28)
- [x] 채팅 서버에 인증 없이 playerId만 넣고 접속 시도 - 거부되는지. 토큰이 비어있으면
      핸드셰이크 자체가 거부되는 것 확인됨 (`SecurityRegressionChecks.cs`)
- [x] 친구 삭제 직후 즉시 메시지 전송 - 클라가 차단 목록으로 조용히 버리는지. 서버가
      `NOT_A_MEMBER`로 1차 차단하는 것까지 확인. 테스트 중 채팅 클라가 서버 Error
      응답에 무한 대기하던 버그 발견/수정 (`KNOWN_ISSUES.md` #29)
- [x] 클라가 시스템 메시지를 흉내낼 수 없는지. `SendMessageRequest`에 발신자/타입
      필드 자체가 없어 구조적으로 불가능함을 확인, 정상 메시지의 발신자가 항상
      정확한 것도 같이 확인됨

### 동시성
- [x] 로비에서 끊긴 직후 그 찰나에 `EnterLobby` 재호출 - 시스템이 안 망가지는지.
      서버 쪽에 인위적 지연을 넣어 race를 넓혀서 재현, 재접속한 세션이 옛 정리
      핸들러에 의해 잘못 지워지지 않는 것 확인됨 (`ConcurrencyChecks.cs`)
- [x] 게임 에이전트 방-채움 타이머 만료와 마지막 클라의 NGO 접속 완료가 겹침. 두
      경로를 임시로 `Task.Yield()` 기반 경합으로 강제 재현(최악의 순서 - 타임아웃
      판정이 먼저 확정된 뒤 게임 시작이 뒤따름)해서 확인 - 에러 없이 방이 정리되고
      클라가 로비로 정상 복귀함
- [x] 로그아웃 직후 채팅 서버로 재접속 시도 - 영구 잔류 안 되는지. 로그아웃된 토큰으로
      재접속은 거부됨 확인(다만 거부되는 경로가 예상과 달랐음 - 클라의 불필요한
      자동 재연결 시도 때문, "구현 필요"의 `WebSocketChatChannel` 항목 참고)
- [x] `AccountServer`가 친구 삭제 이벤트 발행 직전에 크래시 - 재시작 후 아웃박스가
      결국 이벤트를 발행하는지. 확인 결과 발행 안 됨 - `FriendEventOutbox`가 순수
      인메모리라 재시작하면 대기 중이던 이벤트가 그냥 사라짐(이미 알려진 한계,
      새 버그 아님). "`AccountServer`/계정 데이터 진짜 DB 전환" 항목에서 같이
      해결될 예정 - 그 전까지는 친구 삭제 통보가 유실될 수 있다는 걸 감안할 것

### 정책/한계값
- [x] 우편함 상한선 초과 시 밀어내기 순서(수령됨 > 만료됨 > 오래된 것). 상한선 1로
      낮추고 `ItemProvision` 우편 2개 지급 → 정확히 1개만 남는 것 확인
      (`AccountServer`의 `/test/grant-mail` 테스트 엔드포인트 추가)
- [x] `ItemProvision` 우편 자동 수령. 위와 같은 테스트로 같이 확인됨 - 밀려난 우편이
      조용히 증발한 게 아니라 자동 수령 처리된 뒤 삭제되는 것까지 확인
- [x] 동일 계정으로 다른 기기에서 동시 로그인 - 세션 충돌로 거부. 서버는 "다른
      기기"와 "같은 기기의 다른 프로세스"를 구분할 방법도 이유도 없어서, 보안
      회귀의 "다른 서버가 세션을 물고 있을 때 재로그인"과 완전히 같은
      `SESSION_CONFLICT` 경로 - 이미 그때 확인됐고, 테스트 중 계정이 우연히
      겹칠 때마다 실제로도 반복 확인됨
- [x] 매칭 대기 중 클라 연결 끊김 - 자동으로 매칭 서버에서 이탈 처리되는지.
      확인됨(`StayConnected 스트림 종료` → `매칭 취소` 순서). 그 과정에서 버그 2건
      추가 발견/수정: Kestrel에 HTTP/2 keepalive가 없어서 연결 끊김 감지 시간이
      들쭉날쭉했던 것(`KNOWN_ISSUES.md` #30), 이탈 통보가 하드코딩된 가정에
      의존하던 것을 `ConnectedClientInfo` 기반 실제 상태 판단으로 개선(#31)

---

## 구현 필요

- **[메모] `ItemProvision` 우편 자동 수령 시 인벤토리 정책 - 정답 미정** - 실제 인벤토리
  시스템이 없어서 "자동 수령"이 `Claimed=true` 표시로 끝남. 나중에 진짜 인벤토리가
  생기면 `MailboxEvictor`의 `TryClaimAsync` 호출 지점에서 "못 넣으면 증발 vs 최대한
  채워 넣기"를 결정해야 함.
- **로그인 서버 재진입 - 클라 UI 미구현** (백엔드는 완료) - "대참사"(도착지 서버 죽음,
  회전 토큰 2개 다 만료, 로그인 서버 자체 응답 불가) 시 자동 재시도 없이 원인 모르는
  채로 "연결이 끊겼습니다"만 띄우는 화면, 로그인 거부 시 사유 안내 화면.
  `ARCHITECTURE.md` "서버 이동과 재접속" 참고. 접속 시도가 타임아웃으로 포기된 뒤
  (`KNOWN_ISSUES.md` #25) 뭘 할지(로그인 서버로 자동 재진입 시도 / 이 화면만 띄우고
  대기 / 프로그램 종료)도 이 UI 작업과 같이 정할 것 - 순수 취향 문제라 아직 미정.
- **채팅 `RoomInvited`/`RoomRemoved` 클라 UI 미구현** - 디스패처 배선은 끝났지만
  최종 UI 구독자가 없음(`RequestChat` 수락 화면 등 미착수).
- **로그아웃 시 `WebSocketChatChannel`이 의도된 종료를 모르고 자동 재연결을 시도함** -
  "로그아웃 직후 채팅 재접속 시도" 테스트 중 발견: `Logout()` → `SessionInvalidated`로
  서버가 채팅 연결을 강제 종료하면, 클라의 `WebSocketChatChannel`은 이게 로그아웃 때문인지
  모르고 일반 연결 끊김과 똑같이 취급해서 0.5초/1초 뒤 자동 재연결을 시도함(당연히
  실패는 하지만, 사용자 모르게 재연결 시도가 계속 나가는 건 UX상 이상함). `Logout()`을
  호출하는 클라 코드가 명시적으로 `WebSocketChatChannel`한테 "이건 의도된 종료다"라고
  미리 알려줘서 재연결 루프 자체를 안 타게 해야 함 - 로그인 서버 재진입 UI 작업과
  같이 정리하면 좋을 듯.
- **`AccountServer`/계정 데이터 진짜 DB 전환** - `InMemoryAccountRepository`/
  `InMemoryMailRepository` 전부 더미. 친구 목록 아웃박스(`FriendEventOutbox`)도 이때
  같이 DB 트랜잭션으로 묶을 것 - 실제로 발행 직전 크래시 테스트로 유실 확인됨
  (테스트 계획 "동시성" 항목 참고).
- **실력 점수 기반 매칭 알고리즘** - 지금은 더미(1명이면 즉시 매칭). `game_type`별
  대기열 분리도 같이.
- **실배포용 정식 TLS 인증서** - 로컬 개발용 자체 서명 인증서 공유 중.
- **마스터 지시 `Drain` 업무 - 확장 패턴만 검증됨, 실제 로직 미구현** - `Shutdown`/
  `Announcement`(공지)로 `oneof` 확장 구조 자체는 실증 완료. `Drain`은 서버 타입별로
  "신규 접속을 어떻게 거부할지"가 달라서 로직 설계가 아직 없음(매칭 대기열 거부, 로비
  `EnterLobby` 거부 등). `ingame.proto`의 죽은 필드 `GameAgentMessage.drain`도 정리 필요.
- **게임 에이전트의 유니티 프로세스 슈퍼바이저화** - 지금은 완전히 독립된 프로세스.
  나중에 게임 에이전트가 유니티 `.exe`를 자식 프로세스로 스폰/재시작하는 구조로 이전
  예정 - 지금의 하트비트 비대칭(인게임은 에이전트 끊기면 자기 종료, 에이전트는 안 죽고
  버팀)이 그 전제 조건.
- **닉네임 - 계정 서버부터 인게임 클라까지 전 구간에 개념 자체가 없음** -
  proto(계정/로그인/매칭/인게임 전부) 어디에도 `Nickname`/`DisplayName`류
  필드가 없고, 플레이어는 순수 `PlayerId`(문자열)로만 식별됨. 오토체스
  인게임 UI(체력창 등)에서 상대 닉네임을 표시하려면 계정 서버가 닉네임을
  갖고 있다는 전제부터(현재 `InMemoryAccountRepository`에 그런 필드가
  있는지 확인 필요), 로그인/매칭을 거쳐 인게임 서버까지 전달하는 경로
  전체를 새로 설계해야 함. 지금은 Unity 쪽에서 `PlayerId`를 그대로 표시하는
  임시 스텁으로 우회 중.
- **⚠⚠⚠ 우편함 - 클라이언트(Unity) UI 연동 ⚠⚠⚠** - 서버 배선은 전부 완료.
  `ClientInstance.cs`의 실제 수신/UI(메일함 화면, 클릭→확인→개봉, 알림음/배지)는
  의도적으로 미착수. `mail_id` 기반 식별(위치 아닌 안정적 ID)을 UI 코드가 안 지키면
  "확인창 떠있는 동안 배경 갱신" race가 실제로 터질 수 있음 - UI 설계와 같이 논의할 것.
- **`ShutdownNotice`(정상/비정상 종료 구분용) 미발신** - 우선순위 낮음, 지금 개발
  단계엔 안 급함.
- **서킷 브레이커 / Redis Sentinel·Cluster** - 운영 환경 고가용성 확보용, 개발 단계엔
  단일 Redis로 충분. 구현 방식 미정.

---

## 운영/품질 보강 (외부 리뷰에서 나온 항목)

설계 자체보다는 "실서비스 수준으로 검증·운영할 수 있는가"를 보는 관점에서 비어 있는 것들.
일반 백엔드 기준으로 가장 먼저 지적받을 순서대로 정렬했다.

- **데이터 영속성이 전혀 없음** - 위 "`AccountServer`/계정 데이터 진짜 DB 전환" 항목과
  같은 문제의 연장. 계정/우편이 인메모리 더미인 데다 Redis도 `docker-compose`에서
  영속성을 꺼둔 상태(`--save "" --appendonly no`)라, 서버를 재시작하면 모든 데이터가
  사라진다. `IAccountRepository`가 이미 분리돼 있으므로 실제 DB 구현체(PostgreSQL/MySQL +
  EF Core 또는 Dapper)를 추가하는 방향. 세션처럼 휘발돼도 되는 데이터와 계정처럼
  영속돼야 하는 데이터의 경계도 이때 같이 문서화할 것.
- **자동화된 테스트가 없음** - 아래 "통합 테스트 체크리스트"는 전부 수동. 최소한
  `MatchEngine`(대기열/매치 확보/서버 끊김 정리) 단위 테스트와, `RedisSessionRegistry`의
  Lua 스크립트(소유권 이전 승인/거부, 죽은 서버 판정)를 실제 Redis로 검증하는 통합
  테스트(Testcontainers 등)부터. 체크리스트의 장애 주입 항목 중 자동화 가능한 것도
  순차적으로 옮길 것.
- **인증 핵심부가 아직 더미** - 클라 토큰(`DummyTokenProvider` - `"{무작위값}:{playerId}"`
  형식, 서명/만료 없음)과 서버 간 인증(`DummyServerAuthenticator`)이 임시 구현.
  `ARCHITECTURE.md`에 예정으로 적힌 JWT(서명 + 만료 + 회전)와 mTLS로 교체 필요. 위
  "정식 TLS 인증서" 항목과 함께 진행.
- **서비스 자체는 컨테이너화돼 있지 않음** - Docker는 Kafka/RabbitMQ/Redis 인프라에만
  쓰이고, 각 서버는 `start-servers.ps1`이 창을 하나씩 띄우는 방식(Windows/PowerShell
  전용). 서비스별 Dockerfile + 전체 `docker-compose`로 "명령 한 줄로 전체 기동"이 되게
  하고, README에 그 절차를 둘 것.
- **관측성(로그 집계/메트릭/추적)이 없음** - 구조화 로그(`ILogger` 템플릿)는 이미 쓰고
  있지만 수집·조회 수단이 없음. 서비스가 8개라 요청 하나가 여러 서버를 거치는데 이를
  따라갈 방법이 없다 - OpenTelemetry(분산 추적 + 메트릭) 도입 검토.
- **메시징 기술별 역할 정리 문서가 없음** - Kafka/RabbitMQ/Redis Pub/Sub을 모두 쓰는데
  (실제 흐름: `LobbyControlService`, `GameServerHandler`, `FriendEventPublisher` 등),
  각각을 어떤 성격의 메시지(이벤트 로그 / 작업 큐 / 실시간 브로드캐스트)에 왜 쓰는지가
  한 곳에 정리돼 있지 않다. `ARCHITECTURE.md`에 기술별 용도와 선택 이유를 표로 추가할
  것 - "왜 셋 다 필요한가"에 답하지 못하면 기술 나열로 보일 수 있음. `LobbyServer`의
  `/test/kafka`, `/test/rabbitmq/*` 엔드포인트는 개발용임을 명시하거나 Development
  환경에서만 등록되게 할 것.
- **세션 소유권 Lua 스크립트가 JSON을 정규식으로 파싱함** -
  `RedisSessionRegistry.TryClaimOwnershipScript`가 `string.match`로 `OwnerServerId`를
  꺼낸다. 직렬화 형식(필드 순서/공백/이스케이프)이 조금만 바뀌어도 조용히 빈 값이
  나와 "소유자 없음 → 무조건 승인"으로 빠질 수 있음(보안 영향). 세션을 Redis Hash로
  저장해 `HGET`으로 읽거나, 최소한 `cjson.decode`로 파싱하도록 변경.

---

## 알려진 구조적 한계 (완화만 가능, 완전히 없앨 수 없음)

이 경합들은 "판단 시점"과 "실행 시점" 사이에 물리적으로 시간이 걸려서 생기는 것들이라,
비동기 이벤트 체인으로는 창을 0으로 만들 수 없다. 실질적 위험(데이터 오염, 보안 구멍)은
없고 전부 "아깝게 놓쳐서 다시 시도"하는 정도로 우아하게 처리되므로 그대로 감수하기로
결정함.

- **재접 불가능 방에서 "나감 처리" vs "재접속 시도"** - 클라가 끊긴 순간부터 서버 정리가
  끝날 때까지 그 찰나에 클라가 로비에 `EnterLobby`를 부르면 무의미한 안내를 받을 수 있음.
  `EnterLobby` rate limit은 이 경합을 없애는 게 아니라 순수 남용 방지용.
- **게임 에이전트 방-채움 타이머 만료 vs 마지막 클라의 NGO 접속 완료** - 타이머 만료
  판정과 `OnClientConnected` 콜백이 같은 찰나에 겹치면, 방금 접속한 클라도 리다이렉트
  대상으로 취급될 수 있음.
- **친구 삭제 vs 메시지 전송** - `RoomRemoved` 통보 이후 새는 메시지 한 통이 있을 수
  있음. 클라(`WebSocketChatChannel`)가 통보 이후 메시지를 조용히 버리는 "차단 목록"
  방식으로 창을 좁힘.
- **클라 자진 이탈 vs 서버 강제 종료(게임 종료 후 로비 복귀)** - `IngameServerInstance._clients`의
  `ConcurrentDictionary` 원자성으로 방어됨, 실질적 위험 없음.
- **`SessionInvalidated` 발행-소비 지연** - 로비 정리와 채팅 서버 재접속 검증 사이의
  틈. `RemoveAsync`와 발행이 같은 메서드에서 연달아 실행돼서 "영구 잔류"가 아니라
  "잠깐 살아있다가 뒤늦게 정리"로 귀결됨.
- **게임 에이전트의 승인 결과(`ApprovalResolved`)와 메인 스레드 자체 타임아웃이
  겹치는 경우** - 지금은 승인 대기에 자체 타임아웃이 없어서 성립 자체가 안 되는
  경합이지만, 나중에 타임아웃을 추가해도 `PendingApproval == null` 방어 코드가
  구조적으로 이 경합을 흡수함(우연이 아님).

---

## 구현 완료 (요약)

### 인프라 / 마스터 서버
- Redis 세션 레지스트리(`TryClaimOwnershipAsync` 원자적 권한 이전, 서버 생존 TTL 기반).
- 마스터가 매칭/계정/채팅 서버 주소를 전 서버에 동적 배포(대칭 패턴).
- **마스터→메인 서버 지시(`MasterServerInstruction`)**: `Shutdown`(`/admin/shutdown`),
  **공지 방송(`Announcement`, `/admin/announce`)** - `AnnouncementType`(enum) 하나로
  퉁겨서 패킷 양 절감(자유 문자열 안 씀). 로비(`GlobalWorker` + `SyncRoot` 락으로
  "접속 등록"과 "전체 스냅샷 방송" 사이 경합 원천 차단), 게임 에이전트(1:1 연결이라
  단순 릴레이) → 인게임 서버(유니티 단일 스레드라 락 불필요, `CustomMessagingManager`의
  다중 클라 오버로드로 한 번에 전송, `OnClientReady`/`ReadyClientInfo`로 새 접속자한테도
  계속 전달) → 클라(로비/인게임 양쪽 경로 다 배선). `TLS 전체 서버 적용`, `KestrelEndpointExtensions`로
  설정 하나로 일괄 통제.

### 로그인 / 계정 / 토큰
- `LoginServer`/`AccountServer` 신설, 토큰 2슬롯 검증, 비밀번호 해싱(`Pbkdf2PasswordHasher`).
- **`AccountServerIntermediary` 리팩터** - 제3자 대상 전송(`RemoteMailNotifier`)과
  직접 대상 전송(서버별 `MailDeliverer`) 분리, 로비/게임 에이전트/하위 서버 전부 통일.
- **로그인 서버 재진입** - "갈 곳을 잃은 클라"를 위한 안정적 단일 조회 창구(AWS GameLift
  서버리스 매치메이킹 참고). `ReconnectRequest`/`DestinationServerType` 구현+빌드 확인
  완료. 재접속 가능한 방에서 순간적으로 끊긴 경우만 예외(아래 "매칭/재접속" 참고).

### 매칭 / 재접속
- 매칭 정상 흐름 전부 구현, 매칭/로비/인게임 서버 다운 시나리오 처리(`CompensatingTransaction`).
- 재접속 정책: `GameType`별 허용 여부, 재접 가능 방은 유예 타이머로 세션 유지.
- **재접속 빠른 경로 + `RoomPolicy`** - 재접속 가능한 방은 로그인 서버 없이 캐시된
  인게임 주소로 직접 재시도. `ConnectionWelcome`(NGO 접속 완료 시 조건 없이 매번
  `allow_reconnect` 전달, 예전 조건부 `TokenUpdate` 대체)으로 정보를 받고, `RoomPolicy`
  (신규)가 재접속 가능 여부/시도 이력/판단 로직을 캡슐화 - `ClientInstance`는
  `ReconnectAction`(enum) 결과만 보고 실행, 무한 재시도 방지 내장.
- **`IngameServerInstance.OnConnectionApproval` 버그 2건 수정**: (1) `RoomManager`
  명단 대조 없이 클라가 주장하는 playerId를 그대로 믿던 것, (2) `clientInfo.PlayerId`가
  승인 결과 오기 전까지 빈 문자열로 존재하는 찰나가 있던 것.
- **전입 티켓(Transfer Ticket) 시스템** - 목적지 서버가 클라보다 먼저 소유권을 받아두는
  구간에서, 훔친 영구 토큰으로 먼저 도착하는 걸 막는 일회용 짧은 티켓(10초 TTL).
- **세션 소유권 확인 누락 전수조사/수정**, **`AuthInterceptor.ValidateToken`이 세션과
  대조 안 하던 문제 수정** - `GetOwnedSessionOrNullAsync`로 통일.

### 로비 / 세션 생존
- `StayConnected` 스트림(끊김 감지), `SessionInvalidated` 이벤트, `EnterLobby` rate limit.

### 채팅
- Redis 영속 저장, 핸드셰이크 토큰 인증, `RequestChat`/`GetMyRooms`/`GetHistory`.
- `SystemChatDelivery` - 타입 자체로 신뢰 구분. 클라 전체 Unity 신설(`WebSocketChatChannel`).

### 친구 / 우편함
- 친구 시스템(로비/인게임 대리 호출 창구).
- **우편함 시스템** - "메일함/인박스" 패턴(영속 기록 + best-effort 알림). `AccountServer`
  담당(개봉이 보상 지급과 얽힌 경제적 트랜잭션이라 원자적 처리 필요). 접속 시 자동 제공 +
  신호 즉시 제공 + 클라 능동 조회, 로비/게임 에이전트 대칭 구현.
- **우편함 상한선 + 밀어내기 정책** - `mail_type`을 자유 문자열에서 `MailType`(enum,
  `ItemProvision`/`GuildInvite`)으로 전환. `MailboxEvictor`(신규) - 우편함이 상한선을
  넘으면 **수령됨 > 만료됨 > 오래된 것** 순으로 밀어냄(정렬 하나로 세 우선순위 표현).
  미수령 우편은 타입에 따라 `ItemProvision`은 자동 수령 처리 후 삭제, 그 외는 그냥
  삭제. 발동 시점은 클라의 `GetMailList` 요청 시.

### 스레드 안전성 (Unity 클라/서버)
- **`MainThreadDispatcher` 패턴 전면 도입 및 전체 재배선 완료** - 유니티는 출시 빌드에서
  멀티스레드 오작동을 검사하지 않아 조용히 터질 수 있다는 근거로 도입. 타입 있는 데이터만
  큐잉(`Action`/로직 안 넘김), `oneof` 기반 단일 스위치 라우팅. 클라/서버 완전 분리
  (`ClientMainThreadDispatcher`/`IngameServerMainThreadDispatcher`, 이름까지 분리 -
  배포 시 바이너리가 나뉘므로). 예전부터 `IngameServerInstance.cs` 안에 자체 큐로
  파묻혀있던 `_workResults`도 발견해서 통합. 게임 로직 판단이 필요한 이벤트는
  `GameManager`, 순수 네트워크 전달은 `IngameServerInstance`가 소비하는 기준 확정.
  클라/서버 이벤트 전부(로비 전환, 채팅, 우편함, 친구 관리, 방 관리, 공지) 재배선
  완료 - 빈 처리부 없음.

### 기타
- fire-and-forget 함수 전체를 try-catch로 감싸는 관례 확립 및 전체 점검 완료.
- 디버그/더미 코드 위치 컨벤션(`Debug/`, Unity는 `Dummy/`) 통일.
- `Server/KNOWN_ISSUES.md` 신설 - 발견-원인-예상피해-해결-결과 형식 기록.
