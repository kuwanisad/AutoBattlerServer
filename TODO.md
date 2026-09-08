# TODO

미완성 작업과 알려진 구조적 한계를 모아둔다. 완료된 것들의 상세한 발견-원인-해결 경위는
`KNOWN_ISSUES.md`에, 지금 구조가 왜 이렇게 됐는지는 `ARCHITECTURE.md`에 있다 - 여기는
"뭐가 남았는지"만 빠르게 훑는 용도로 간결하게 유지한다.

---

## 구현 필요

- **[메모] `ItemProvision` 우편 자동 수령 시 "인벤토리 못 넣으면 증발 vs 최대한 채워줌"
  - 정답 미정** - 우편함 상한선 밀어내기(`MailboxEvictor`) 구현 중 나온 열린 질문.
  지금은 실제 인벤토리 시스템 자체가 없어서 "자동 수령"이 그냥 `Claimed=true` 표시만
  하는 걸로 끝나는데, 나중에 진짜 인벤토리 시스템이 생기면 그 지점(`MailboxEvictor`가
  `TryClaimAsync`를 부르는 자리)에서 "인벤토리가 꽉 차서 못 넣으면 그냥 우편을 증발시킬지,
  아니면 어떻게든 자리를 만들어서 최대한 채워 넣을지"를 결정해야 함.
- **로그인 서버 재진입 - 클라 UI만 아직 미구현** - 백엔드 로직(`ReconnectRequest`,
  `HandleReconnectAsync`, `DestinationServerType`, `TryReconnectViaLoginServerAsync`)은
  구현+빌드 확인 완료(아래 "구현 완료" 참고). 남은 건 클라 UI뿐:
  - **"대참사" 시나리오**(도착지 서버 죽음, 회전 토큰 2개 다 만료, 로그인 서버 자체
    응답 불가) 발생 시 자동 재시도 없이 "연결이 끊겼습니다"만 띄우고 사람이 직접
    재시도하게 하는 화면 - 원인을 안다는 전제가 깔린 조언("잠시 후 다시 시도")도
    붙이면 안 됨(TODO.md 하단 상세 근거 참고).
  - **로그인 서버가 정상 응답했지만 거부한 경우**(자격증명 틀림 등) - 연결 안 끊고
    사유만 안내(다시 입력하면 바로 재시도 가능한 정상 흐름, "연결 실패"와 다르게 취급).
  - `ARCHITECTURE.md`의 "서버 이동과 재접속" 섹션에 설계 배경 상세 기록.
- **Unity 클라/게임 서버 스크립트의 스레드 안전성 - 명시적 방어 도입, 부분 재배선 완료** -
  업계 조사 결과(유니티 공식 문서) 결정 근거를 찾음: (1) 네트워킹처럼 지연 우려 시 커스텀
  동기화를 유니티 자신이 권장, (2) `SynchronizationContext` 캡처 자체에 성능 비용 존재,
  (3) 결정적으로 - **유니티는 실제 출시(비개발) 빌드에서는 멀티스레드 오작동 검사/에러
  표시를 안 함** - `ConfigureAwait(false)`가 실수로 들어가도 배포 빌드에서 조용히 터질
  수 있다는 뜻. 그래서 `ConcurrentQueue` 기반 "메인 스레드 디스패처" 패턴(업계에 실제로
  흔한 패턴, `UnityMainThreadDispatcher` 등 여러 레퍼런스 확인)을 도입하기로 확정.
  - **설계**: `Action`(로직)이 아니라 **타입 있는 데이터만** 큐에 넣음(백그라운드는
    "무슨 일이 있었는지"만 알려주고 "뭘 할지"는 절대 안 넘김) - `object` + `is` 타입
    검사도 지양(캐스팅 비용), 대신 proto `oneof`(`PayloadCase`, 정수 비교)로 구분.
    `Dispatch`는 "브로드캐스트 후 각자 확인"이 아니라 **한 곳(스위치)에서 정확히 어느
    함수를 부를지 결정**하고, 실제 처리 로직만 기능 영역별 `partial` 파일로 나눔.
    클라/서버(`IngameServerInstance`)는 **완전히 분리된 별도 proto 프로젝트**(배포 시
    바이너리가 나뉘므로, 합치면 서버 내부 이벤트 구조를 클라 빌드에서 추측할 여지를
    준다고 판단) - `ClientDispatch.Contracts`(`ClientDispatchEvent`) /
    `IngameServerDispatch.Contracts`(`IngameServerDispatchEvent`). 둘 다 gRPC 서비스
    없이 순수 메시지만(`GrpcServices="None"`).
  - **완료된 재배선**: 클라 - `MatchFound`(로비→인게임 전환), 채팅 4종(`ChatMessageDelivery`/
    `SystemChatDelivery`/`RoomInvited`/`RoomRemoved` - `WebSocketChatChannel`의 옛
    `OnMessageReceived`/`OnRoomInvited`/`OnRoomRemoved` 이벤트 체인은 완전히 제거,
    `ChatMessageInfo` DTO는 `GetHistoryAsync` 전용으로만 남김 - 그건 요청-응답이라
    범위 밖). 서버 - `MailProvided`, `FriendCommandResult`, `ApprovalResolved`,
    `RedirectDisconnect`(**예전부터 `IngameServerInstance.cs` 안에 자체 큐로 파묻혀
    있던 `_workResults`/`ClientWorkResult`를 발견해서 이 디스패처로 완전히 통합함** -
    `Update()`의 수동 소비 루프 제거).
  - **클라/서버 클래스명 분리**: `MainThreadDispatcher`라는 이름을 클라/서버 양쪽이
    똑같이 쓰고 있던 게 그 자체로 위험 요소라고 판단(로그/스택 트레이스만 봐서 구분
    불가, 코드 읽는 사람 혼동) - `ClientMainThreadDispatcher`(클라)/
    `IngameServerMainThreadDispatcher`(서버)로 분리.
  - **소비자 선택 기준 확정**: 게임 로직 판단이 필요한 이벤트(`OnOpenRoomReceived`/
    `OnCloseRoomReceived`처럼 `RoomManager` 등에 위임되는 것)는 `GameManager`가
    소비하고, 순수 네트워크 전달/상태 갱신인 이벤트(오늘 재배선한 것들)는
    `IngameServerInstance`가 직접 소비 - 이미 있던 `IngameServerInstance ↔ GameManager
    ↔ CompleteWork` 왕복 패턴과 일치시킴.
  - **재배선 전부 완료(서버/클라)**: 서버(`GrpcIngameServerChannel`) - `OnOpenRoomReceived`
    (`GameManager` 소비)/`OnCloseRoomReceived`(`IngameServerInstance` 소비 - 겉보기와
    달리 "판단"이 아니라 NGO 클라 정리의 전제 조건으로 `RoomManager.CloseRoom`을
    부르는 것뿐이라)/`OnDrainReceived`(지금은 `Debug.Log`만, `Drain` 업무 자체가
    미구현이라 실제 로직 생기면 그때 다시 볼 것). `OpenRoomRequest`/`CloseRoomInfo`/
    `PlayerRedirectInfo` DTO 전부 제거하고 원본 protobuf(`IngameServer.OpenRoom`/
    `CloseRoom`) 직접 사용으로 통일.
    클라 - `RoomInvited`/`RoomRemoved`는 케이스까지는 만들어놨지만 최종 UI 구독자가
    없어서(`RequestChat` 수락 화면 등 미착수) 처리부는 비워둠(`ClientMainThreadDispatcher.Chat.cs`
    참고) - 이건 여전히 미완성.
- **`IngameServerInstance.OnConnectionApproval` 진짜 버그 2건 발견/수정**:
  (1) `RoomManager` 명단(`GetRoomIdForPlayer`) 대조 없이 클라가 페이로드로 주장하는
  playerId를 그대로 믿고 게임 에이전트한테 검증을 넘기고 있었음 - 이제 로비/매칭
  서버가 `OpenRoom`으로 사전 등록해둔 명단에라도 있는지부터 확인해서, 없으면 게임
  에이전트한테 물어볼 것도 없이 즉시 거부. (2) `clientInfo.PlayerId`를 채우는 코드가
  아예 빠져있어서, 승인 결과가 오기 전까지 그 값이 빈 문자열로 존재하는 찰나가
  있었음(`OnClientConnected`/`OnClientDisconnected`의 `clientId.ToString()` 폴백이
  실제로 발동할 수 있었던 이론적 원인) - 명단 대조를 통과한 시점에 즉시 채우도록 수정.
- **`ConnectionWelcome` 메시지 신설(재접속 빠른 경로의 사전 준비)**: 예전 `TokenUpdate`
  (새 토큰 있을 때만 조건부 전송)를 대체 - NGO 접속 완료 시 조건 없이 항상 전송,
  새 토큰 + `allow_reconnect`(이 방이 재접속 가능한 타입인지)를 같이 실어보냄.
  `Room.FillConnectionWelcome`(가상 메서드, 기본은 `AllowReconnect`만 채움) +
  `RoomManager.FillConnectionWelcome`(위임 - `Room` 객체 자체는 밖으로 안 내보내는
  기존 캡슐화 원칙 유지).
- **재접속 빠른 경로 구현 + `RoomPolicy` 객체화**: 재접속 가능한 방에서 순간적으로
  끊기면, 로그인 서버를 안 거치고 캐시해둔 인게임 주소로 직접 재시도(`ReconnectAction.Reconnect`).
  딱 한 번만 시도 - 그 서버가 진짜 죽어있어 재시도까지 실패하면 다음 끊김부턴
  `ReconnectAction.ReLogin`으로 자연 폴백(무한 재시도 방지). 관련 상태(재접속 가능
  여부, 시도 이력, 주소)와 판단 로직을 `ClientInstance`에 흩어진 플래그로 안 두고
  `RoomPolicy`(신규, 클라)로 캡슐화 - `ClientInstance`는 enum(`ReconnectAction`)
  결과만 보고 실행, "로그인 서버를 거친다"는 구체적 사실은 `RoomPolicy`가 전혀 모름.
  향후 방 관련 다른 판단도 이 객체로 계속 모을 예정(사용자 의도).
- **로비는 재접속(재개) 개념 없음 - 확정** - 인게임은 `GetAllowReconnect(gameType)`이
  있는 방은 끊겨도 세션/방 상태를 그대로 두고 재접속 시 이어서 진행하지만, 로비는
  `StayConnected`가 끊기면(순간적인 네트워크 블립이라도) 유예 없이 그 즉시 세션을
  지운다(`HandleStayConnectedEndedAsync`) - 이 비대칭을 없애는 대신, 위 "로그인 서버
  = 안정적 단일 조회 창구" 흐름으로 흡수하기로 결정. 매칭 대기 중(`RequestMatch` 스트림
  보유 상태) 순간 끊김도 대기열에서 바로 빠지는(`RequestLeaveMatchAsync`) 채로 유지 -
  재접속 시 대기열 자리 복원 기능은 만들지 않음.
- **`AccountServer`/계정 데이터의 진짜 DB 전환** - `InMemoryAccountRepository`/
  `InMemoryMailRepository` 전부 여전히 더미. 친구 목록 아웃박스(`FriendEventOutbox`)도
  이때 같이 DB 트랜잭션으로 묶을 것.
- **실력 점수 기반 매칭 알고리즘** - 지금은 더미(1명이면 즉시 매칭). `JoinMatchRequest`의
  `game_type`별 대기열 분리도 이때 같이 구현.
- **실배포용 정식 TLS 인증서** - 지금은 로컬 개발용 자체 서명 인증서(`dotnet dev-certs`)
  공유 중. 실제 도메인 생기면 Let's Encrypt 등으로 교체.
- **마스터 지시(`MasterServerInstruction`)의 `Drain` 업무** - `Shutdown`만 구현됨. 신규
  접속/매칭 접수를 서버 타입별로 다르게 중단시키는 로직 필요(매칭 서버 대기열 거부,
  로비 `EnterLobby` 거부 등). `task` `oneof`에 새 케이스 추가하면 됨 - 확장 구조는 이미
  갖춰져 있음. `ingame.proto`의 죽은 필드 `GameAgentMessage.drain`도 이때 같이 정리.
- **게임 에이전트의 유니티 프로세스 슈퍼바이저화** - 지금은 게임 에이전트/인게임 서버가
  완전히 독립된 프로세스로 뜸. 나중에 게임 에이전트가 유니티 `.exe`를 자식 프로세스로
  직접 스폰/재시작하는 구조로 갈 예정 - 지금의 하트비트 비대칭(인게임은 게임 에이전트
  끊기면 자기 종료, 게임 에이전트는 안 죽고 버팀)이 이 미래 구조의 전제 조건.
- **⚠⚠⚠ 우편함 - 클라이언트(Unity) UI 연동 ⚠⚠⚠** 서버 배선은 전부 완료(로비/게임
  에이전트 양쪽 접속 시 자동 제공 + 신호 즉시 제공 + 클라 능동 조회, `AccountServerIntermediary`
  리팩터까지 완료). **`ClientInstance.cs`의 실제 수신/UI(메일함 화면, 클릭→확인→개봉,
  알림음/배지)는 의도적으로 미착수.** `mail_id` 기반 식별(위치가 아니라 안정적 ID로
  참조), 딕셔너리를 진짜 데이터로 두고 정렬 리스트는 파생 뷰로만 사용 - 이 원칙을 UI
  코드가 안 지키면 "확인창 떠있는 동안 배경 갱신" 류의 race가 실제로 터질 수 있다. UI
  설계와 반드시 같이 논의해서 진행할 것.
- **`ShutdownNotice`(옛 정상/비정상 종료 구분용) 미발신** - `MasterConnectionService`에
  `StopAsync` 오버라이드가 없어서 이 메시지가 한 번도 안 나감(항상 "비정상 종료"로만
  로그 남음). 우선순위 낮음 - `start/stop-servers.ps1`로 테스트하는 지금 단계엔 안 급함.
- **서킷 브레이커 / Redis Sentinel·Cluster** - 운영 환경 고가용성 확보용, 개발 단계에선
  단일 Redis로 충분. 구현 방식/폴백 동작은 미정.

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

---

## 구현 완료 (요약)

### 인프라 / 마스터 서버
- Redis 세션 레지스트리(`TryClaimOwnershipAsync` 원자적 권한 이전, 서버 생존 TTL 기반
  - 세션 자체 TTL은 폐지), `SessionRegistryConnector`(지연 연결 프록시 패턴).
- 마스터가 매칭/계정/채팅 서버 주소를 전 서버에 동적 배포(대칭 패턴: 신규 등록 시
  브로드캐스트 + 기존 등록자 캐치업, 순서 무관).
- **마스터→메인 서버 지시(`MasterServerInstruction`)**: `Shutdown` 업무 구현 및 실제
  검증 완료(`/admin/shutdown`, 인스턴스/타입/전체 타겟 지정). 타겟 정보는 메시지에
  안 담고 발행 시점에만 판단.
- `RegistryCleanupServer`/마스터의 `RequestIngameAssignment` 등 죽은 코드 정리 완료.
- **TLS 전체 서버 적용**(로컬 개발 인증서 기준), `KestrelEndpointExtensions`로
  `shared.appsettings.json`의 `UseTls` 값 하나로 일괄 통제.

### 로그인 / 계정 / 토큰
- `LoginServer`/`AccountServer` 신설 - 클라 최초 진입 전담, 계정 검증/생성.
- 토큰 2슬롯(`CurrentToken`/`PreviousToken`) 실제 대조 검증, 로비/인게임 양방향 전부
  구현(`TokenVerification.IsValid` 공용). 토큰은 무작위값(`"{무작위값}:{playerId}"`).
- 비밀번호 해싱(`Pbkdf2PasswordHasher`) - 평문 저장 문제 발견 후 수정.
- **`AccountServerIntermediary` 리팩터** - AccountServer API를 감싼 범용 추상 클래스
  (`Shared.Common`, 메일 전용 아님, 나중에 아이템/밴/VIP 등으로 확장 가능). 제3자 대상
  전송(`RemoteMailNotifier`, 세션 레지스트리로 소유 서버 조회)과 직접 대상 전송(로비/
  게임 에이전트 각자의 `MailDeliverer`)을 분리 - "지금 나랑 붙어있는 클라냐 제3자냐"가
  기준. 로비/게임 에이전트/하위 서버 전부 이 구조로 통일, 예전 `CreateAccountServerChannel`
  중복 완전 제거.
- **로그인 서버 재진입 - "갈 곳을 잃은 클라"를 위한 안정적 단일 조회 창구** - AWS
  GameLift 서버리스 매치메이킹 사례(클라는 특정 서버 주소를 직접 캐시/재시도하지
  않고 항상 안정적인 단일 창구한테 "지금 상태 뭐야"를 물어봄) 참고, 우리 시스템에선
  로그인 서버=그 창구, 세션 레지스트리(Redis)=내부 상태 저장소에 대응. `ReconnectRequest`
  (playerId+세션 토큰, 자격증명 재인증 아님)/`HandleReconnectAsync`(살아있는 소유자
  있으면 안내만, 소유권 자체는 안 건드림 - 없으면 새로 시작)/`DestinationServerType`
  (Lobby/Ingame 구분) 구현+빌드 확인 완료. 재접속 가능한 방에서 순간적으로 끊긴
  경우만 예외로 이 경로를 안 타고 캐시된 인게임 주소로 직접 재시도(아래 "매칭 / 재접속"
  참고) - 목적지가 아직 살아있다는 게 전제인 시간 민감한 경우라 로그인 서버를 거치면
  오히려 손해. 남은 건 클라 UI뿐(위 "구현 필요" 참고).

### 매칭 / 재접속
- 매칭 정상 흐름 1~8번 전부 구현. 매칭 서버 책임 범위는 8번(권한 이전)까지, 이후는
  게임 에이전트가 직접 처리(자체 방-채움 타임아웃 `RoomTimeout`/`PendingKill`).
- 재접속 정책: `GameType`별로 허용 여부 결정, 재접 가능 방은 유예 타이머(기본 30초)로
  세션 유지. `MatchToken` 완전 제거(`OwnerServerId` 기반 검증으로 대체).
- 매칭/로비/인게임 서버 다운 시나리오 전부 처리(`CompensatingTransaction`, `_matchLock`
  원자화, 클라 리다이렉트).
- **재접속 빠른 경로 + `IngameServerInstance.OnConnectionApproval` 버그 2건 수정** -
  재접속 가능한 방에서 순간적으로 끊기면 로그인 서버를 안 거치고 캐시된 인게임
  주소로 직접 재시도(`ConnectionWelcome` 메시지로 클라가 매번 `allow_reconnect`를
  받아둠, `RoomPolicy`가 시도 이력 캡슐화해서 무한 재시도 방지). 이 과정에서 진짜
  버그 2건 발견/수정: (1) `RoomManager` 명단(`GetRoomIdForPlayer`) 대조 없이 클라가
  페이로드로 주장하는 playerId를 그대로 믿고 있었음, (2) `clientInfo.PlayerId`를
  채우는 코드가 아예 빠져있어서 승인 결과 오기 전까지 빈 문자열로 존재하는 찰나가
  있었음.
- **전입 티켓(Transfer Ticket) 시스템** - 목적지 서버가 클라보다 먼저 소유권을 받아두는
  구간(로비↔인게임 전환)에서, 훔친 영구 토큰으로 진짜 클라보다 먼저 도착하는 걸 막는
  일회용 짧은 티켓(`MainServerIntermediary`, 10초 TTL, `Shared.Common`). 진짜 세션
  토큰은 안 건드림. `LobbyMainServerIntermediary`/`IngameAgentMainServerIntermediary`가
  각자 발급 측/수신 측 역할을 대칭적으로 수행. 클라(Unity)까지 왕복 완료
  (`MatchFound.transfer_ticket`/`PlayerRedirect.transfer_ticket`, `ConnectionApproval`
  페이로드/`EnterLobbyRequest`에 실어 제출). `ARCHITECTURE.md` "서버 이동과 재접속"
  섹션에 전체 흐름 상세 기록.
- **세션 소유권 확인 누락 - 전수조사 및 수정** - `LobbyControlService`의
  `RequestMatch`/`CancelMatch`/`Logout` 세 곳이 "이 로비가 실제 세션 소유자인지" 확인
  없이 세션을 수정하고 있었음(발견 경위: 전입 티켓 설계 논의 중 우연히 발견).
  `GetOwnedSessionOrNullAsync` 헬퍼로 통일 - 소유권(`OwnerServerId`) + 토큰 유효성
  (`TokenVerification.IsValid`) 둘 다 확인. 게임 에이전트 쪽은 유니티가 항상 자기
  짝 게임 에이전트하고만 1:1 통신하는 구조라 같은 위험이 원천적으로 없음을 확인.
- **`AuthInterceptor.ValidateToken`이 세션과 실제로 대조 안 하고 있었음** - 위 전수조사
  중 발견. `_tokenProvider.ValidateToken`은 토큰 문자열의 형식만 확인하지, 세션의
  `CurrentToken`/`PreviousToken`과 대조하지 않는다 - 그러니 이미 회전으로 무효화됐어야
  할 옛 토큰도 형식만 맞으면 `AuthInterceptor`를 그냥 통과한다. 진짜 대조는
  `EnterLobby`/`ConnectionApproval`(소유권을 새로 클레임하는 지점)에서만 하고 있었음.
  `GetOwnedSessionOrNullAsync`가 이제 이 대조까지 겸해서, 위 세 RPC는 이 구멍이 막힘 -
  다른 RPC(`GetFriends` 등)는 세션을 안 건드리는 읽기 전용이라 이 문제의 실질적
  영향권 밖. `GrpcChannelFactory`(신규, `Shared.Common`)로 채널 생성 로직도 같이 정리
  (`AccountServerIntermediary`에서 분리 - 그 클래스와 무관한 범용 유틸리티였음).

### 로비 / 세션 생존
- `StayConnected` 스트림 - 로비에 조용히 앉아있는 클라의 끊김 감지(하트비트 없이 스트림
  생존 여부로 판단). 매치 확정 중 정상 이동과는 `OwnerServerId`+`Status` 확인으로 구분.
- `SessionInvalidated` 이벤트 - 세션이 지워지는 모든 지점에서 발행, 채팅 서버가 구독해
  강제 종료.
- `EnterLobby` rate limit(`IRateLimiter`, Redis `SET NX PX`).

### 채팅
- 전면 재설계 - Redis 영속 저장(`IChatRoomStore`/`IChatMessageStore`, 인스턴스 로컬
  문제 해결), 핸드셰이크 토큰 인증(회전 없음), `RequestChat`/`GetMyRooms`/`GetHistory`.
- `SystemChatDelivery` - 타입 자체로 신뢰를 구분(필드 값 기반 규약은 스푸핑 위험으로
  기각). 친구 삭제-메시지 전송 race는 클라 측 "차단 목록" 방식으로 방어.
- Unity 클라 전체 신설(`IChatChannel`/`WebSocketChatChannel`) - 채팅만 지수 백오프
  자동 재연결(인게임과 다른 정책).

### 친구 / 우편함
- 친구 시스템(로비/인게임 양쪽 대리 호출 창구, 채팅 서버는 `RequestChat` 검증만 예외적으로 직접 조회).
- **우편함 시스템** - 업계 표준 "메일함/인박스" 패턴 채택(실제 상태 변경은 영속 기록,
  알림은 best-effort). `AccountServer`가 담당(채팅 서버 아님 - 개봉이 보상 지급과
  얽힌 경제적 트랜잭션이라 원자적 처리가 필요해서). `AccountServerIntermediary`/
  `RemoteMailNotifier`/서버별 `MailDeliverer`로 배선 완료 - 접속 시 자동 제공(안전망) +
  신호 즉시 제공(best-effort 최적화) + 클라 능동 조회(`GetMailList`), 로비/게임
  에이전트 대칭 구현. `mail_id` 기반 식별 원칙 확립(위 "구현 필요"의 UI 경고 참고).
- **우편함 - 상한선 + 밀어내기 정책** - `mail_type`을 자유 문자열에서 `common.proto`의
  `MailType` enum(`ItemProvision` - 점검/장애/이벤트 보상 등 전부 통합, `GuildInvite` -
  이득 불분명 예시)으로 전환. `MailboxEvictor`(신규, `AccountServer`) - 우편함이
  `AccountServerConfig.MailboxCapacity`(테스트 중이라 지금 2, 검증 끝나면 30으로
  변경 예정)를 넘으면 밀어냄. 밀어내는 순서는 **수령됨 > 만료됨 > 오래된 것** -
  정렬 하나(`OrderByDescending(Claimed).ThenByDescending(만료여부).ThenBy(생성시각)`)로
  세 단계 우선순위를 한 번에 표현. 아직 미수령 상태로 밀려나야 하는 우편은 타입에 따라
  다르게 처리 - `ItemProvision`(보상)은 밀려나기 전에 자동 수령 처리(`TryClaimAsync`),
  그 외는 그냥 삭제(거부). 발동 시점은 `CreateAsync`가 아니라 클라의 `GetMailList`
  요청 시점(`AccountControlService`). `IMailRepository.DeleteAsync` 신설.
  RabbitMQ 테스트 메시지의 `mailType` 필드는 이제 enum의 C# 멤버 이름(PascalCase,
  예: `"ItemProvision"`)으로 넣어야 함(`JsonStringEnumConverter` 사용) - proto 원본
  이름(`"ITEM_PROVISION"`)이 아님에 주의.

### 기타
- fire-and-forget 함수는 함수 전체를 try-catch로 감싸는 관례 확립 및 전체 점검 완료
  (`ARCHITECTURE.md` "코딩 관례" 참고).
- 디버그/더미 코드 위치 컨벤션(`Debug/`, Unity는 `Dummy/`) 통일.
- `Server/KNOWN_ISSUES.md` 신설 - 발견-원인-예상피해-해결-결과 형식 기록.
