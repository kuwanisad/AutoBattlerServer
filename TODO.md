# TODO

미완성 작업과 알려진 구조적 한계를 모아둔다. 완료된 것들의 상세한 발견-원인-해결 경위는
`KNOWN_ISSUES.md`에, 지금 구조가 왜 이렇게 됐는지는 `ARCHITECTURE.md`에 있다 - 여기는
"뭐가 남았는지"만 빠르게 훑는 용도로 간결하게 유지한다.

---

## 구현 필요

- **로그인 서버 = "갈 곳을 잃은 클라"를 위한 안정적 단일 조회 창구 - 설계 확정, 구현 전**
  - **발단**: 인게임→로비 전환 중 `RoomClosed`(전입 티켓 포함)를 클라가 못 받는 경우
    (네트워크 완전 단절 등), 서버 쪽(세션, 전입 티켓 등록)은 이미 정리가 다 끝났는데
    클라만 갈 곳을 모르게 됨. 지금 `LoginConnectionHandler`는 이미 살아있는 소유자가
    있으면(`TryClaimOwnershipWithPreviousOwnerAsync` 실패) 그냥 `SESSION_CONFLICT`로
    거부해서, 클라가 이 경로로 복구할 방법이 없었음.
  - **업계 사례 조사**: AWS GameLift의 서버리스 매치메이킹 - 클라는 특정 서버 주소를
    직접 캐시/재시도하지 않고, 항상 안정적인 단일 창구(API Gateway)한테 "지금 상태
    뭐야"를 물어봄. 그 창구가 내부적으로 상태 저장소(DynamoDB)를 조회해서 알려주는
    구조. 이 철학을 그대로 가져옴 - **우리 시스템에서 그 "안정적 단일 창구" 역할이
    로그인 서버, "내부 상태 저장소" 역할이 세션 레지스트리(Redis)**에 대응.
  - **최종 확정된 흐름**:
    1. 클라가 (어떤 경로로든) 접속이 끊김.
    2. **재접속 가능한 방에서 순간적으로 끊긴 경우만 예외** - 기존에 갖고 있던 인게임
       주소로 직접 재시도(빠른 경로, 이미 있는 `ConnectionApproval` 토큰 대조 복귀
       메커니즘 그대로 유지 - 목적지가 아직 살아있다는 게 전제인 시간 민감한 경우라
       로그인 서버를 거치면 오히려 손해).
    3. 그 외 모든 경우(로비는 재접속 개념 자체가 없으니 무조건 / 재접속 불가능한 방 /
       2번 시도가 접속 거부로 실패) → 클라가 로그인 서버한테 `playerId` + **세션
       토큰**(계정 자격증명 재인증 아님 - 매번 아이디/비번 재입력은 사용성 문제)을 제출.
    4. 로그인 서버가 세션 레지스트리 조회 - 살아있는 소유자가 있으면(`OwnerServerId` +
       그 서버의 하트비트 TTL 확인) 토큰 대조 후 그 주소로 안내만 함(소유권 자체는
       안 건드림, 로그인 서버가 다시 클레임할 필요 없음). 소유자가 없으면(세션 자체가
       없거나 소유 서버 하트비트 만료) 완전히 새로 시작(기존 로직 그대로, 새 로비 배정).
    5. **"목적지 서버가 죽었다"는 판단은 클라가 직접 접속 시도 실패로 알게 되는 것**이지,
       로그인 서버가 능동적으로 감지해서 알려주는 게 아님 - 로그인 서버는 그냥 다음
       조회 시점에 세션 레지스트리의 하트비트 TTL이 자연스럽게 만료된 걸 보고 판단.
  - **번외 - 이동 중 도착지 서버가 죽거나 회전 토큰(`CurrentToken`/`PreviousToken`)
    2개 다 만료된 "대참사" 시나리오**: 클라가 접속 거부당해서 로그인 서버로 가면,
    로그인 서버 입장에선 세션 레지스트리에 유효한 기록이 없어 보일 수 있어(도착지
    서버 죽음의 경우 그 서버 하트비트 TTL이 아직 안 끝났을 수도 있음) 완전히 새로
    로그인해야 하는 상황이 될 수 있음. **근데 이 경우 클라가 자동으로 즉시 로그인
    서버로 재시도하는 게 오히려 안 좋을 수 있다는 결론**:
    - 서버 죽음 케이스: 세션 레지스트리가 "죽었다"고 실제로 판단하려면 그 서버의
      하트비트 TTL이 자연스럽게 만료될 시간이 필요함 - 클라가 그 전에 즉시 재시도해봤자
      의미 없음.
    - 토큰 2개 다 만료 케이스: 이게 일어났다는 건 서버가 그 사이 두 번이나 회전할
      만큼 응답이 느렸다는 뜻(서버가 바쁘다는 신호) - 이 순간 클라가 자동으로 즉시
      재접속을 쏟아내면 이미 부하 걸린 서버에 기름을 붓는 꼴.
    - **그러니 이 "대참사" 케이스는 자동 복구 로직을 만들지 말고, 그냥 클라 화면에
      "연결이 끊겼습니다"만 띄우고 사람이 직접 재시도(버튼 등)하게 두는 게 낫다는
      결론** - 사람이 누르기까지의 지연 자체가 (1) TTL이 자연스럽게 만료될 시간을
      벌어주고 (2) 서버 부하 완화(자연스러운 스로틀링) 효과가 있음. 아직 구현 전,
      클라 UI 설계 시 이 판단 기준(재접속 가능 방 순간 끊김만 자동 재시도, 나머지는
      사람 개입 필요)을 반영할 것.
  - **로그인 서버 자체가 몰려서 응답 불가 상태가 되는 경우도 같은 원칙** - 이건
    "최후의 안전망"(로그인 서버)마저 없어진 셈이라 목적지 서버 하나 죽는 것보다
    더 심각함. **두 가지를 명확히 구분해야 함**:
    - **로그인 서버가 정상 응답했지만 요청을 거부한 경우**(자격증명 틀림 등) - 연결을
      끊지 않고, 그냥 화면에 사유를 안내만 함(다시 입력하면 바로 재시도 가능한 정상
      흐름). 이건 "연결 실패"가 아니라 "입력 오류"라 성격이 다름.
    - **로그인 서버와의 연결 자체가 실패한 경우**(타임아웃/거부) - **클라는 원인을
      알 방법이 없음**(자기 쪽 네트워크 문제인지, 로그인 서버가 몰려서인지 구분 불가).
      그러니 "잠시 후 다시 시도해주세요"처럼 원인을 안다는 전제가 깔린 조언조차
      붙이면 안 되고, **의도적으로 뭉뚱그린 표현**("서버에 접속할 수 없습니다" 등)만
      띄움. 위 "대참사" 원칙과 동일하게 자동 재시도 없음 - 이미 몰려있는 로그인
      서버한테 클라들이 자동으로 재시도를 쏟아부으면 복구가 더 늦어짐.
  - `ARCHITECTURE.md`의 "서버 이동과 재접속" 섹션에 상세 기록.
- **Unity 클라/게임 서버 스크립트의 스레드 안전성 - 암묵적 전제에 의존 중** - `GrpcLobbyChannel`/
  `GrpcIngameServerChannel`/`WebSocketChatChannel`의 이벤트(`OnMatchFound`,
  `OnMailProvided` 등)를 구독하는 `ClientInstance`/`IngameServerInstance`의 핸들러들이
  유니티 API(`NetworkManager`, `RoomManager` 등)를 직접 건드리는데, 이게 항상 메인
  스레드에서 실행된다는 보장이 **명시적 검증이 아니라 "코드 어디에도 `ConfigureAwait(false)`/
  `Task.Run`이 없다"는 암묵적 사실 하나에 전적으로 의존**하고 있음(전수 검색 결과 현재는
  없음 확인됨 - 유니티의 `SynchronizationContext`가 `await` 재개 지점을 메인 스레드로
  돌려주는 표준 동작에 의존). 문제는: (1) `ConfigureAwait(false)`는 일반 .NET 개발/라이브러리
  코드에서 흔한 관례라 나중에 무심코 추가되면 컴파일 에러 없이 조용히 깨짐, (2)
  `Grpc.Net.Client`/`YetAnotherHttpHandler` 등 외부 라이브러리 내부 구현에 있을 가능성도
  배제 못 함(버전업 시 흔들릴 수 있음). 명시적 방어(예: 델리게이트 호출부에 메인 스레드
  여부 검증)를 추가할지, 아니면 지금처럼 암묵적 전제로 둘지 미정.
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
- **우편함 - 상한선 + 밀어내기 정책** - `InMemoryMailRepository`가 무제한으로 쌓임. DB
  전환 시 같이 구현(예: 최대 100개, 오래된 것부터 삭제).
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

### 매칭 / 재접속
- 매칭 정상 흐름 1~8번 전부 구현. 매칭 서버 책임 범위는 8번(권한 이전)까지, 이후는
  게임 에이전트가 직접 처리(자체 방-채움 타임아웃 `RoomTimeout`/`PendingKill`).
- 재접속 정책: `GameType`별로 허용 여부 결정, 재접 가능 방은 유예 타이머(기본 30초)로
  세션 유지. `MatchToken` 완전 제거(`OwnerServerId` 기반 검증으로 대체).
- 매칭/로비/인게임 서버 다운 시나리오 전부 처리(`CompensatingTransaction`, `_matchLock`
  원자화, 클라 리다이렉트).
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

### 기타
- fire-and-forget 함수는 함수 전체를 try-catch로 감싸는 관례 확립 및 전체 점검 완료
  (`ARCHITECTURE.md` "코딩 관례" 참고).
- 디버그/더미 코드 위치 컨벤션(`Debug/`, Unity는 `Dummy/`) 통일.
- `Server/KNOWN_ISSUES.md` 신설 - 발견-원인-예상피해-해결-결과 형식 기록.
