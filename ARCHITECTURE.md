# 아키텍처 (ARCHITECTURE)

---

## 설계 철학

**단일 책임 원칙**
각 서버는 하나의 역할만 담당한다. 마스터 서버가 매칭까지 담당하거나, 로비 서버가 채팅까지 담당하는 식의 비대화를 지양한다. 기능이 추가될 때 기존 서버를 수정하는 대신 새 서버를 추가하는 방향을 선호한다.

**인터페이스 격리**
통신 수단에 직접 의존하지 않는다. `IIngameServerChannel`, `ILobbyChannel`, `IChatChannel` 같은 인터페이스로 구현체를 격리해서, 나중에 통신 방식이 바뀌어도 비즈니스 로직은 수정하지 않아도 된다.

**현업 관행 우선**
설계 결정이 필요할 때 현업에서 검증된 패턴을 먼저 찾는다. 매칭 대기열에 Redis Sorted Set(예정), 서버 간 메시지 동기화에 Redis Pub/Sub, 인증에 JWT, 채팅에 WebSocket + Protobuf를 쓰는 것이 그 예다. 접속 끊김/장애 처리 설계 시에도 상태 패턴, 사가(보상 트랜잭션), 이벤트 버스 등 현업 패턴을 비교 검토한 뒤 채택한다.

**점진적 구현**
더미 구현체(`DummyTokenProvider`, `DummyChatFilter`, `DummyServerAuthenticator` 등)로 껍데기를 먼저 완성하고, 실제 구현체로 교체하는 방식을 취한다. 인터페이스가 안정적이면 구현체 교체가 다른 코드에 영향을 주지 않는다.

**상태의 단일 진실 공급원**
플레이어 상태(로비/매칭/이동중/인게임)와 "지금 이 플레이어를 누가 책임지는지"(`OwnerServerId`)는 Redis Session Registry가 단독으로 관리한다. 어느 서버든 Redis를 조회하면 현재 플레이어의 정확한 상태를 알 수 있다. 이를 통해 서버 이동 시 발생할 수 있는 보안 취약점(타인 사칭)을 차단한다. Session Registry는 "관리 권한 기록부"로만 쓴다 - 방 상태처럼 각 서버가 스스로 알고 있어야 할 업무 데이터는 여기 두지 않고 각 서버 로컬 메모리에 둔다(예: 매칭 서버의 `_matches`, 인게임 서버의 `RoomManager`).

**감지와 판단의 분리**
연결 끊김/장애를 "감지"하는 코드(스트림 종료 콜백 등)와 "그래서 뭘 해야 하는지 판단"하는 로직을 분리한다. 감지 계층은 상태를 소유한 쪽의 진입점 하나만 호출하고, 실제 정리/복구 로직은 그 안에 캡슐화한다 (`PendingWorkTracker.Cleanup()`, `GameManager.Shutdown()`, `MatchEngine.HandleServerDisconnected()` 등).

---

## 구조도

```
클라이언트 (Unity)
  │
  ├── gRPC (로비 상태)
  │     ↓
  │   로비 서버 (.NET 8, 5200/5201)
  │     ├── 마스터 서버와 gRPC 연결 유지
  │     ├── 매칭 서버와 접속 유지 스트림 (MatchServerConnection)
  │     └── Redis: 세션 등록/업데이트 (주소는 마스터가 동적으로 배포)
  │
  ├── WebSocket + Protobuf (항상 유지)
  │     ↓
  │   채팅 서버 (.NET 8, 8000/8001)
  │     ├── 마스터 서버와 gRPC 연결 유지
  │     └── Redis: 채널 멤버 관리, 세션 조회
  │
  └── NGO/UTP (인게임 상태)
        ↓
      게임 서버 (Unity Dedicated Server, 7777)
        │  (GameManager → RoomManager로 실제 방 생성/관리)
        └── gRPC (7000/7001)
              ↓
            게임 에이전트 (.NET 8)
              ├── 마스터 서버와 gRPC 연결 유지
              ├── 매칭 서버와 접속 유지 스트림 (MatchServerConnection, Unity Ready 신호 대기 후 접속)
              ├── Redis: 세션 업데이트, ConnectionApproval 검증 (세션 상태 + OwnerServerId + 전입 티켓)
              └── 판단은 안 함 - 부하/재접속 허용/유예 시간은 게임 서버(Unity)가 정하고 결과만 받아 실행

마스터 서버 (.NET 8, 5100/5101)
  ├── 모든 메인 서버(로비/게임 에이전트/채팅/매칭)와 gRPC 연결 유지
  ├── 접속 서버 인증 (IServerAuthenticator - 지금은 더미, 추후 mTLS)
  ├── 인스턴스 레지스트리 관리
  ├── 매칭 서버 주소를 메인 서버들에 동적 배포
  └── 세션 레지스트리(Redis) 주소를 모든 접속 서버에 동적 배포

매칭 서버 (.NET 8, 6000)
  ├── 로비/게임 에이전트와 접속 유지 스트림(MatchConnectionControl) - 등록/생존 감지/
  │   매칭 결과 통보(MatchNotification)를 전부 이 스트림으로 처리
  ├── JoinMatch/LeaveMatch(로비↔매칭), PrepareMatch/PrepareMatchResult(매칭↔게임에이전트)
  │   - 요청-응답이 필요한 것만 별도 단항 RPC로 유지
  ├── 접속 끊김 시 CompensatingTransaction으로 진행 중이던 매치를 되돌림
  ├── GameType별 대기열 + 정원(MatchingConstants) - 정원이 다 차야 매치 생성(기본 규칙)
  └── [예정] Redis Sorted Set 기반 실력 점수 매칭 - 지금은 먼저 온 순서

하위 서버들 (.NET 8)
  ├── 보상 서버: RabbitMQ work queue 소비 [예정]
  ├── 결제 서버: RabbitMQ work queue 소비 [예정]
  └── 더미 서버: 개발용 (DummySubServer)

브로커
  ├── Kafka: 매치 결과, 게임 이벤트 (분석/비동기) [예정 - 아직 아무도 발행 안 함]
  ├── RabbitMQ: 세션 무효화/친구 삭제 이벤트, 보상/결제 (work queue) - 모든 서버가 RabbitMqBroker로 접속
  └── Redis: Session Registry(관리 권한 기록부), 매칭 대기열(예정), 채팅 Pub/Sub
```

---

## 각 요소의 기능

### 클라이언트 (Unity)

로비 상태와 인게임 상태 두 가지로 존재하며, 채팅 서버와는 항상 연결을 유지한다.

- **로비 상태**: gRPC로 로비 서버와 통신한다. 로그인, 로비 입장(`EnterLobby`), 매칭 요청, 채팅 등을 처리한다.
- **인게임 상태**: NGO(Unity Transport)로 게임 서버와 통신한다. 실시간 게임 동기화를 담당한다.
- **채팅**: WebSocket + Protobuf으로 채팅 서버와 로그인~로그아웃 내내 연결을 유지한다. 로비/인게임 상태와 무관하게 항상 활성 상태다.
- **전환**: 매치 확정 시 로비 gRPC 채널을 닫고 NGO로 게임 서버에 접속한다. 이 시점에 `playerId` 페이로드만 포함한다(예전엔 `matchToken`도 같이 보냈으나 폐지 - 매칭 서버가 게임 에이전트에 플레이어 명단을 직접 전달하므로, 게임 에이전트가 `OwnerServerId` 검증만으로 충분하다).
- **로비 입장 시퀀스**: `Login`(순수 인증, 토큰만 발급) → `EnterLobby`(세션 관리 권한 획득 시도). 다른 서버(주로 인게임)가 아직 살아서 권한을 갖고 있으면 거부되고, 그 서버 주소가 함께 온다 - 클라는 이걸로 "진행 중인 게임이 있습니다" 안내를 띄울 수 있다.
- **재접속 정책**: 재접속 허용 여부와 유예 시간은 방의 게임 모드가 정한다 - `ReconnectableGameMode`(유예 시간 필드 포함, 오토체스)와 `NonReconnectableGameMode` 중 하나를 상속한다. 재접속 가능 방에서 끊기면 방의 게임 모드가 유예 시간을 재고, 그 안에 돌아오면 그대로 이어서 플레이, 지나면 탈락 확정 후 게임 에이전트가 세션을 삭제한다(재로그인 필요). 탈락 확정된 플레이어의 재접속은 `PLAYER_ABANDONED`로 거부된다. 재접속 불가 방에서 끊기면 즉시 탈락하고 세션 소유권이 풀려 아무 로비로나 돌아갈 수 있다. 상세는 아래 "서버 이동과 재접속" 참고.
- **실패 사유 표시**: 서버는 거부/실패 사유를 문장이 아니라 proto 열거형(`LoginRejectReason`, `EnterLobbyResult`, `MatchFailReason`, `FriendResult`, `ApprovalDecision`)으로 보내고, 클라 쪽 실패(응답 없음, RPC 오류 등)는 Unity 전용 `ClientFailure`로 구분한다. 화면 문구는 `EnumToScreenText`(언어별 테이블, 기기 언어로 선택)에서 찾는다 - 서버가 함께 보내는 `detail` 문자열은 로그/디버그용이라 화면에 띄우지 않는다. 인게임 접속 거부는 NGO 사유 필드가 문자열뿐이라 `ApprovalDecision`의 숫자 값을 보내고 클라가 되돌린다.
- **ClientInstance**: `DontDestroyOnLoad` 싱글톤. 씬 전환 시에도 유지되며 `LobbyClient`, `ChatClient`, 추후 `IngameClient`를 들고 있다. 언리얼의 `GameInstance`에 대응하는 개념.

### 마스터 서버

모든 메인 서버(로비, 게임 에이전트, 채팅, 매칭)의 허브 역할을 한다.

- 각 서버 인스턴스의 부하/수용량을 추적하는 인스턴스 레지스트리를 관리한다.
- **접속 서버 인증**: `IServerAuthenticator` 퍼사드로 접속하는 서버를 검증한다. 지금은 `DummyServerAuthenticator`(항상 통과)이고, 나중에 mTLS 도입 시 여러 검증기를 조합하는 `CompositeServerAuthenticator`로 교체할 수 있게 설계했다.
- **매칭 서버 주소 배포**: 메인 서버가 최초 접속하면(기동 순서는 마스터→매칭→메인) 이미 등록된 매칭 서버 주소를 즉시 전달한다. 매칭 서버가 재등록되면(재시작 등) 접속 중인 메인 서버 전체에 브로드캐스트한다.
- **계정 서버/채팅 서버 주소 배포**: 매칭 서버와 완전히 같은 대칭 패턴(신규 등록 시 브로드캐스트 + 기존 등록자 대상 캐치업)으로 배포한다. 실제로는 로그인 서버만 이 두 주소를 쓰지만(계정 검증/생성, 로그인 성공 응답에 채팅 주소 포함), 순서 걱정 없이 어느 서버가 먼저 뜨든 동작한다.
- **세션 레지스트리(Redis) 주소 배포**: 마스터가 `shared.appsettings.json`에서 읽은 Redis 주소를 접속하는 모든 서버 타입(매칭 서버 포함)에 최초 등록 시 바로 전달한다. 메인 서버들은 더 이상 Redis 주소를 로컬 설정에서 직접 읽지 않는다.
- **메인 서버 지시(`MasterServerInstruction`)**: 마스터가 특정 메인 서버(들)에게 업무를 지시한다. "누구한테 보낼지"(특정 인스턴스/특정 서버 타입/전체)는 메시지 자체엔 안 담고, 마스터가 발행하는 순간(`/admin/shutdown` 엔드포인트)에만 판단한다 - 받는 서버는 자기가 어떻게 선택돼서 그 메시지를 받았는지 몰라도 된다. `task`를 `oneof`로 둬서 확장 가능하게 설계했다(지금은 `ShutdownTask`만 있음, 새 업무 종류는 여기 케이스만 추가). 마스터↔메인 서버 간 이미 있던 상시 스트림(하트비트/주소 배포와 같은 통로)을 그대로 쓰고, 새 연결은 안 만든다. `MasterConnectionService`가 기본 동작(유예 시간 대기 후 정상 종료)을 수행하고, 서버 타입별 커스터마이즈가 필요하면 `OnShutdownRequested` 이벤트를 구독하면 된다. 실제 배포 시 수동으로 서버들을 하나씩 끄던 걸 이걸로 대체하는 게 목표. **[미구현] `Drain`**: 신규 접속/매칭 접수를 중단시키는 업무는 서버 타입별로 뭘 해야 할지 달라서 아직 설계 전 - `task` `oneof`에 새 케이스로 추가하는 확장 구조는 이미 갖춰져 있음.
- **전달 실패 기록**: 인스턴스 큐에 메시지를 못 넣으면(접속 중이 아니거나 큐가 닫힘) `InstanceRegistry.Enqueue`가 false를 돌려주고 Warning을 남긴다. `/admin/shutdown`·`/admin/announce`는 실제 전달 결과(`대상 N개 중 M개 전달, 실패: ...`)로 응답한다(예전엔 못 보내도 "전달"이라고 응답).
- **재접속 시 상태 재전송**: 쌓아뒀다 다시 보내지 않고, 인스턴스가 (재)접속하면 "현재 상태"를 한 번 보낸다 - 주소 정보(매칭/계정/채팅/Redis)와 켜진 공지(`ActiveAnnouncements`가 종류+대상 범위로 기억). 종료 지시 같은 1회성 명령은 재전송하지 않는다(실패가 응답으로 드러나므로 운영자가 다시 보냄). 마스터가 재시작되면 이 기억도 사라진다.
- 매칭/채팅 등 비즈니스 로직은 담당하지 않는다.
- (레거시) `RequestIngameAssignment` RPC가 남아있으나 매칭 서버 도입 이후 아무도 호출하지 않는다 - 정리 대상.

### 로비 서버

클라이언트가 로비 상태에서 접속하는 서버다.

- 클라이언트와 gRPC로 통신한다. `Login`/`CreateAccount`(순수 인증), `EnterLobby`(권한 획득), `Logout`, `RequestMatch`, `CancelMatch`를 처리한다.
- JWT 기반 인증을 담당한다. `AuthInterceptor`가 모든 요청(Login/CreateAccount 제외)에서 토큰을 자동 검증한다.
- `EnterLobby`에서 세션 레지스트리에 관리 권한을 요청한다(`TryClaimOwnershipWithPreviousOwnerAsync`) - 다른 서버가 아직 살아서 소유 중이면 거부되고 그 주소를 클라에 돌려준다.
- `RequestMatch` 시 매칭 서버에 `JoinMatch` 요청 후, 매칭 서버가 접속 유지 스트림으로 push하는 `MatchNotification`을 `MatchNotificationDispatcher`가 받아서 대기 중인 클라 스트림으로 전달한다. `JoinMatch`가 거부되거나(지원하지 않는 GameType, 이미 대기 중) 호출 자체가 실패하면 그 사유로 `MatchFailed`를 보내고 끝낸다(예전엔 결과를 안 보고 무조건 "대기열 진입"을 보내서, 대기열에 없으니 타임아웃도 안 걸려 클라가 영원히 기다렸다).
- 매칭 대기 중 클라 연결이 끊기면 자동으로 매칭 서버에 `LeaveMatch`를 요청한다(정상 종료 vs 연결 끊김을 `CancellationToken` 출처로 구분).
- 마스터 서버와 gRPC 연결을 유지하며 부하를 주기적으로 보고한다. 매칭 서버와도 별도의 접속 유지 스트림(`MatchServerConnection`)을 연다.

### 채팅 서버

로비와 인게임 경계를 넘나드는 채팅을 담당한다. 표층 서버로 클라이언트와 직접 연결된다.

- 클라이언트와 WebSocket + Protobuf으로 로그인~로그아웃 내내 연결을 유지한다.
- 마스터 서버와 gRPC로 등록/상태 보고를 한다.
- Redis에서 플레이어 세션을 조회해 채팅 허용 여부를 판단한다.
- Redis Pub/Sub으로 채팅 서버 인스턴스 간 메시지를 동기화한다.
- 채널 종류: 친구(`friend:{friendshipId}`), 귓말(`whisper:{userA}:{userB}`), 길드(`guild:{guildId}`)
- 인게임 내 채팅(팀/매치)은 NGO가 담당하며 채팅 서버와 무관하다.
- `IChatMessageStore`로 메시지 저장을 추상화한다. 현재는 인메모리 구현체라 인스턴스 이동 시 과거 기록을 못 가져오는 문제가 있음 - Redis/DB 기반 공유 저장소로 교체 예정 (`TODO.md` 참고).
- 필터링은 `IChatFilter`로 추상화돼 있으며 현재는 더미 구현체 사용 (inline 방식, 즉시 차단/허용).
- Kafka/RabbitMQ 프로듀서(`AddMainServerMessaging`)가 등록만 돼 있고 실제로 쓰이는 곳은 없음 - 모더레이션 감사 파이프라인 등 용도가 명확해지면 정리.

### 게임 서버 (Unity Dedicated Server)

NGO 기반 인게임 서버다.

- `IngameServerInstance`(옛 `IngameServerBootstrap`)가 게임 에이전트와 gRPC 연결을 맺은 후 NGO 서버를 시작한다. 채널(`IIngameServerChannel`)을 들고 있으면서 `GameManager`와 게임 에이전트 사이를 중계하는 허브 역할만 한다 - 클라 쪽 `ClientInstance`와 대응되는 구조.
- `GameManager`(정적 클래스, 매니저 총괄)가 `RoomManager`(방 여러 개 동시 관리, 유저↔방ID 매핑 캡슐화) 등 하위 매니저를 생성/관리한다. `NetworkManager`(NGO)도 `NetworkManagerAdapter`로 감싸서 같은 정리 목록에 편입시킨다.
- 방 생성 흐름: 게임 에이전트(`OpenRoom` 수신) → `IngameServerInstance`(해석, workId 발급) → `GameManager`(작업 분배) → `RoomManager`(실제 방 생성, 결과는 `GameErrorCode`로) → `GameManager` → `IngameServerInstance.CompleteWork` → `RoomOpened` 응답 전송.
- `ConnectionApprovalCallback`에서 클라이언트의 `playerId`를 게임 에이전트에 전달해 검증을 위임한다(예전엔 `matchToken`도 같이 보냈으나 폐지). 직접 Redis에 접근하지 않는다.
- 검증 결과(`ApprovalResponse`)를 받아 NGO 접속을 승인하거나 거부한다.
- **부하(자리 수)를 세는 유일한 곳**: `ServerLoadTracker`가 (방, 자리) 집합으로 센다 - 자리는 사람이든 게임 모드가 채운 봇이든 한 자리. `OnClientEntered`/`OnClientExited` 델리게이트로만 바뀌고, 바뀔 때마다 `GameServerStatus`로 게임 에이전트에 보고한다. 응접실 생성 때 예정 인원을 임시로 잡고(수락~승격 사이 정원 초과 방지), 승격 시 게임 모드가 알려준 실제 자리(`GameMode.GetOccupiedSeatKeys`, 봇 포함)로 교체, 탈락 확정/재접속 불가 방 이탈/방 종료 때 뺀다. 정원(`IngameServerInstance._capacity`)도 같은 "자리" 단위다.
- **재접속 판단**: 끊김을 받으면 방의 게임 모드가 판단한다(`Room.OnClientDisconnected`) - 재접속 가능 모드는 InGameId를 인덱스로 쓰는 배열에 만료 시각을 기록하고 매 프레임 확인(`ReconnectableGameMode.TickFramework`, `Time.unscaledTime` 기준), 불가 모드는 즉시 탈락 확정. 탈락은 `Room.ReportAbandoned` → `Room.PlayerAbandoned` 이벤트 → `RoomManager` → `IngameServerInstance.HandlePlayerAbandoned`(자리 해제 + 유예 만료면 게임 에이전트에 `PlayerAbandoned`). 게임 에이전트에는 끊김 시 `PlayerLeft(reconnectable)`로 판단 결과만 알린다. 방이 닫히면 게임 모드와 함께 탈주자 기록도 사라진다.
- **응접실 승격**: 승격을 시작한 응접실은 `_pendingDrawingRooms`로 옮겨(대기 중인 `_drawingRooms`와 구분) 승격 절차 동안에도 접속 완료/끊김/승인 요청/방 종료가 계속 반영되게 한다. 닉네임 조회도 승격 절차의 일부라 `NetworkTimeouts.NicknameFetchTimeout`(5초) 안에 응답이 없거나 로스터 중 한 명이라도 닉네임이 없으면(승격 중 계정 삭제 등) 방을 깨뜨린다(`RoomTimeout`에 사유를 실어 보고 → 게임 에이전트가 접속자 로비 복귀 + `CloseRoom`). 로스터는 기다림이 끝난 시점의 접속자 중 실제 NGO 접속이 있는 사람으로 만들고, 빠진 예정 인원은 탈락으로 알린다(응접실이 승격 없이 닫힐 때도 같음).
- 게임 에이전트와의 연결이 끊기면(하트비트 타임아웃 포함) `GameManager.Shutdown()`이 `ManagerType` 순서(Room→Network)대로 전부 정리 후 프로세스를 종료한다 - 되돌릴 수 없는 절차로 확정했다("게임 에이전트를 되살려서 재연결"하는 경로는 TODO로 보류).

### 게임 에이전트

게임 서버(Unity)의 사이드카 역할을 한다. Unity에서 직접 처리하기 어려운 서버 로직을 담당한다.

- 게임 서버와 gRPC 양방향 스트림으로 연결된다.
- `ConnectionApproval` 검증을 담당한다. 세션 상태가 `Moving`(서버 이동 - 전입 티켓 필수) 또는 `Reconnecting`(재접속 - 토큰만)이고 소유자가 이 서버여야 승인한다(`TryClaimOwnershipAsync`로 원자적 이전 + 토큰 회전). `MatchToken` 검증은 폐지됨.
- 플레이어 접속/퇴장 시 Redis Session Registry를 업데이트한다. 퇴장은 게임 서버가 보낸 `PlayerLeft.reconnectable`대로 `Reconnecting`(소유권 유지) 또는 소유권 해제만 실행하고, 유예 만료(`PlayerAbandoned`)를 받으면 "아직 재접속 대기 중이고 소유자가 나"인지 확인한 뒤 세션을 지운다 - 재접속 허용 여부/유예 시간은 판단하지 않는다(예전의 `GetAllowReconnect`/30초 타이머 제거).
- 부하는 세지 않는다 - 게임 서버가 보낸 `GameServerStatus`를 저장해 마스터에 전달만 한다. `PrepareMatch` 수용 판단은 "보고된 부하 + 수락했지만 아직 보고에 반영 안 된 자리"(`GameServerInfo.TryReserveSeats`, `RoomOpened`를 받으면 해제)로 한다.
- 매칭 서버와 접속 유지 스트림(`MatchServerConnection`)을 연다 - 단, Unity 게임 서버가 실제로 Ready를 보낼 때까지는 접속을 미룬다(`SignalReady`) - 준비 안 된 게임 에이전트가 매칭 후보로 잡히면 안 되므로.
- 매칭 서버로부터 `PrepareMatch`를 받으면 수용 가능 여부만 즉시 응답하고, 실제 방 생성은 `GameServerHandler.RequestOpenRoomAsync`(게임 서버에 `OpenRoom` 전송 후 `RoomOpened` 응답을 `PendingWorkTracker`로 대기)로 처리한 뒤 결과를 `PrepareMatchResult`로 별도 보고한다.
- 게임 서버와의 연결이 끊기면 대기 중이던 모든 비동기 요청(방 생성 등)을 일괄 실패 처리한다(`PendingWorkTracker.Cleanup()`).
- 매치 결과를 Kafka에 발행한다. (예정)
- `clientId ↔ playerId` 매핑을 관리한다.

### 매칭 서버

매치메이킹 전담 서버다.

- **로비/게임 에이전트와 접속 유지 스트림(`MatchConnectionControl.Connect`)**: 마스터 서버의 `Connect` 패턴과 동일. 접속 자체가 등록이고, 스트림이 끊기면(하트비트 타임아웃 포함) 즉시 서버 풀에서 제거된다 - 예전 `RegisterServer`/`UnregisterServer` 단항 호출 + Redis TTL 폴링을 대체했다.
- **매칭 확정/취소 통보**: 예전엔 `LobbyMatchControl.NotifyMatch`가 이중 정의(로비 쪽 구현체 없음)로 로비에 아예 안 닿는 버그가 있었는데, 이제 위 접속 유지 스트림에 `MatchNotification`을 실어 보내는 방식으로 바뀌면서 자연스럽게 해결됐다.
- **접속 끊김/장애 처리**: `Shared.Common/Transactions/CompensatingTransaction`(범용 되돌리기 스택, 매칭 도메인과 무관하게 재사용 가능)을 각 `PendingMatch`가 필드로 들고 있다가, 중단되면 단계별로 쌓인 부작용을 역순으로 되돌린다. `MatchEngine`의 `_onMatchDisrupted` 델리게이트 딕셔너리가 "지금 어느 단계인지"에 따른 정책만 결정하고, 실제 실행은 `Transaction.Rollback()`에 위임한다. `Queued`/`PreparingRoom` 단계는 구현 완료, `Crossing`(방 열림 ~ 전원 접속 완료) 단계는 게임 에이전트→매칭 서버 "접속 완료" 보고 경로가 아직 없어서 미구현.
- 매치 확정 시 `PrepareMatch`로 게임 에이전트에 방 생성을 요청하고 `PrepareMatchResult`로 결과를 받는다(응답을 실제로 기다림 - 예전엔 100ms 딜레이로 흉내만 냈음).
- **GameType별 대기열**: `MatchingConstants`(타임아웃 30초, 루프 간격 1초, GameType별 `MatchRule{RequiredPlayers, Shortfall}`)를 기준으로, 같은 GameType끼리 정원만큼 모였을 때만 매치를 만든다(기본 규칙 `RequireFull` - 덜 모이면 기다리다 타임아웃). 규칙이 없는 GameType(Unspecified/None/Deleted)은 `JoinMatch`에서 `UNSUPPORTED_GAME_TYPE`으로 거부. Normal 정원은 테스트 동안 1(원래 8 - 사람 1 + 봇 7).
- **매치 중단(`DisruptMatch`)**: 사유(`MatchDisruption` - `PlayerLeft`/`LobbyDisconnected`/`IngameServerDisconnected`/`PrepareRejected`/`PrepareFailed` + 대상 ID)를 받아 안에서 누구를 대기열로 되돌릴지 정한다 - 이탈한 플레이어와 끊긴 로비 소속은 증발, 나머지는 복귀. 복귀 시 `GameType`과 처음 대기열에 들어온 시각을 그대로 되살린다(예전엔 둘 다 잃어서 매칭 타임아웃이 영원히 안 걸렸다).
- **연결에 묶인 메시지는 버퍼링하지 않는다**: 로비/게임 에이전트 연결이 끊기면 양쪽 다 그 연결에 딸린 상태를 버린다(매칭 쪽 `HandleServerDisconnected`, 로비 쪽 `MatchNotificationDispatcher.HandleConnectionLost`가 대기 중인 클라에 `MATCHING_SERVER_DISCONNECTED`). 재접속 때 밀린 매치 확정을 다시 보내면 이미 실패를 통보받은 클라에게 닿아 아무도 안 오는 방이 생기므로, 기록 없이 버리는 게 의도된 동작이다.
- [예정] Redis Sorted Set으로 실력 점수 기반 매칭 대기열을 관리한다. 지금은 먼저 온 순서.
- [예정] 매칭 수락 화면 지원.

### Redis Session Registry

플레이어와 서버의 "관리 권한 기록부"다. 방 상태 같은 각 서버의 업무 데이터는 여기 두지 않는다(각 서버 로컬 메모리에서 관리 - 예: 매칭 서버의 `_matches`, 인게임 서버의 `RoomManager`. 방은 그 방을 연 서버와 운명을 같이 하므로 외부에 복제해봤자 의미가 없다는 판단).

- 모든 서버가 동일한 Redis를 바라봐서 어느 서버든 플레이어의 현재 상태를 조회할 수 있다.
- **주소는 정적 설정이 아니라 마스터가 동적으로 배포한다.** 접속하는 서버는 `ISessionRegistryConnector`(→ 실제 구현 `SessionRegistryConnector`)로 주소를 받을 때까지 대기했다가 연결한다 - 그 전에 `ISessionRegistry`를 호출하는 코드는 자동으로 대기 상태가 된다.
- 플레이어 상태: `Lobby → Moving → Ingame → (끊김 시 Reconnecting → Ingame) → Moving → Lobby` (`Matching`은 검사하는 곳이 없고 세션 리크만 만들어서 제거됨). `Reconnecting`은 저장 형식(정수)이 안 바뀌게 열거형 맨 뒤에 추가했다.
- `Moving` 상태는 전입 티켓을 들고 서버를 옮기는 중임을 나타낸다(이동 대상 = `OwnerServerId`, 접속할 주소 = `NextServer`). 이 상태로 도착한 클라는 반드시 티켓 검증을 거친다. 재접속 불가 방에서 끊겨 소유자를 비운 경우(`OwnerServerId` 빈 값)도 이 상태를 쓰는데, 그땐 아무 로비나 티켓 없이 받는다.
- `Reconnecting` 상태는 재접속 가능 방에서 끊겨 소유 게임 에이전트로 다시 붙기를 기다리는 중임을 나타낸다(티켓 없이 토큰만). 게임 에이전트가 `PlayerLeft(reconnectable=true)`를 받을 때, 또는 로그인 서버가 `Ingame` 상태인 세션을 인게임으로 재진입시킬 때 바꾼다(로그인 서버는 `Moving`인 세션은 건드리지 않는다 - 덮어쓰면 티켓 검증을 건너뛰는 구멍).
- **서버 등록/하트비트 분리**: 서버 기동 시 `RegisterServerAsync(id, address)`로 ID+주소를 한 번만 등록하고, 이후 `RefreshServerHeartbeatAsync`는 `EXPIRE`로 TTL만 갱신한다(값 재전송 없음 - 하트비트 부하 최소화). 하트비트 간격 5초 / TTL 10초.
- **소유권 이전**: `TryClaimOwnershipAsync`/`TryClaimOwnershipWithPreviousOwnerAsync`(Lua 스크립트로 원자적 처리). 세션이 없으면 무조건 승인(최초 로그인), 기존 소유자가 죽어있으면 승인, 살아있으면 거부(+ 후자는 기존 소유자 주소를 같이 반환해서 `EnterLobby`가 "진행 중인 게임이 있습니다" 안내에 쓸 수 있게 함).
- 세션 자체엔 TTL이 없다. 재접속 유예 시간 안에 실패하거나(게임 서버의 `PlayerAbandoned`), 전입 티켓 TTL 안에 목적지에 도착하지 않으면(받는 쪽 서버의 `OnTicketExpired`) 세션을 `RemoveAsync`로 완전 삭제한다(재로그인 필요).

### 브로커

**Kafka**: 매치 결과, 게임 이벤트, 채팅 로그 등 분석/집계가 필요한 대용량 이벤트를 처리한다. 수십ms 지연이 있어 매칭같은 실시간 처리에는 부적합하다. (아직 아무도 실제로 발행하지 않음 - 프로듀서 유틸만 준비돼 있음)

**RabbitMQ**: 보상 지급, 결제 처리, 신고 처리 등 정확히 한 번 처리가 보장돼야 하는 작업과, 서버 간 이벤트(`SessionInvalidated`, 친구 삭제)에 쓴다. Work queue 패턴으로 하위 서버들이 소비한다. (하위 서버 자체가 아직 더미만 있음)

모든 서버가 `Shared.Common.Messaging.RabbitMqBroker`(BackgroundService) 하나로 접속한다(`AddRabbitMqBroker`) - 예전의 `RabbitMqProducer`(발행할 때마다 접속, 실패하면 유실)/`RabbitMqConsumer`(첫 접속 실패 시 서버 시작 실패)/`FriendEventOutbox`를 대체했다.
- **접속**: 백그라운드에서 성공할 때까지 `ReconnectIntervalSeconds`(5초)마다 재시도(`ReconnectPolicy`). 자동 복구는 끄고 재접속 루프 하나만 쓴다. 재접속되면 등록된 구독을 다시 연다.
- **발행**: 대상(큐/fanout exchange)별 `OutboundBuffer`에 쌓았다가 접속되면 순서대로 보내고, publisher confirm을 받은 뒤에만 버퍼에서 지운다. 버퍼 한도는 기본 무제한(-1 - 보상처럼 잃으면 안 되는 메시지), 채팅처럼 최신만 중요한 대상만 `RabbitMQ:BufferLimits`로 한도를 둔다(넘치면 오래된 것부터 버림). 발행자가 큐를 미리 선언하므로 소비자보다 먼저 떠도 메시지가 사라지지 않는다. 권한/존재/형식 오류(403/404/406)는 그 대상만 멈추고 메시지는 보존한다.
- **한계**: 버퍼는 메모리에만 있어서 서버가 재시작되면 아직 못 보낸 메시지는 사라진다(파일 영속화는 구조가 커서 하지 않기로 결정).

**Redis**: Session Registry(관리 권한 기록부), 매칭 대기열(예정), 채팅 Pub/Sub에 쓴다. 인메모리라 응답이 빠르고 TTL로 자동 만료가 가능하다.

---

## 서버 이동과 재접속

클라가 로그인→로비→인게임→로비를 오가는 각 전환 지점에서, 정상 흐름과 그 도중 발생 가능한 이벤트(끊김, 실패, 타임아웃)를 각각 어떤 클래스/함수가 처리하는지 정리한다.

### 로비 → 인게임 전환

**정상 흐름**: `LobbyControlService.RequestMatch`(클라가 스트림을 염) → `MatchNotificationDispatcher.HandleMatchNotificationAsync`(매칭 서버로부터 확정 알림을 받음, `Confirmed` 분기) → 세션을 `Moving`+`OwnerServerType.Ingame`으로 갱신 → `LobbyMainServerIntermediary.IssueAndNotifyAsync`(전입 티켓 발급 + 게임 에이전트의 `IngameControlService.RegisterIncomingTransfers`에 미리 등록) → `MatchFound`(티켓 포함)를 스트림에 씀 → 클라(Unity)가 `ClientInstance.OnMatchFound`에서 받아 `NetworkManager.StartClient()`로 접속 → 유니티 서버의 `IngameServerInstance`가 `OnConnectionApproval`에서 페이로드(`playerId|token|ticket`)를 파싱해 `GameServerHandler.HandleApprovalRequestAsync`에 검증 위임(토큰 대조 `TokenVerification.IsValid` + 소유 전환이면 `IngameAgentMainServerIntermediary.TryConsume`) → 승인되면 `RoomManager.RegisterConnectedClient`.

**발생 가능한 이벤트와 대응**:
- **`RequestMatch` 스트림이 매칭 대기 중 끊김** — `context.CancellationToken.IsCancellationRequested`를 `finally`에서 감지, `RequestLeaveMatchAsync`로 매칭 서버 대기열에서 즉시 제거. **재접속 시 대기열 자리를 이어서 복원하는 기능은 없음**(TODO.md "로비의 재접속(재개) 개념 부재" 참고) - 다시 처음부터 대기열에 진입해야 함.
- **훔친 영구 토큰으로 진짜 클라보다 먼저 인게임 서버에 도착 시도** — `TryConsume`은 맞는 티켓일 때만 원자적으로 1회 통과시키고 지우므로, 먼저 온 쪽이 통과하면 진짜 클라는 이미 소비된 티켓으로 거부됨(`INVALID_TRANSFER_TICKET`). 티켓 검증 필요 여부는 세션 상태로 판단한다 - `Moving` + 소유자가 이 서버면 반드시 티켓이 있어야 한다(예전엔 "티켓이 아직 있는가"로 판단해서, TTL로 지워진 뒤 온 클라는 재접속으로 간주돼 검증 없이 통과했다). 틀린 티켓으로는 진짜 티켓을 지우지 않는다(지우면 만료 이벤트가 안 와서 세션이 영원히 남았다).
- **첫 인게임 접속이 실패함(이동 중)** — 클라는 아직 `ConnectionWelcome`을 못 받아 로그인 서버로 재진입한다. 로그인 서버는 `Moving`이면 상태를 그대로 두고 `NextServer`(이동할 곳)로 안내하고(예전엔 `CurrentServer` - 아직 로비 주소 - 를 안내해서 실패가 반복됐다), 클라는 MatchFound로 받아둔 티켓이 같은 주소용이면 그걸 다시 낸다(`ClientInstance._ingameTransfer`). 티켓 TTL(10초)이 지나면 받는 쪽 서버가 세션을 지우고, 클라는 처음부터 다시 로그인한다.
- **재접속 가능한 방에서 클라가 순간적으로 끊김** — 게임 서버의 게임 모드가 유예 시간 감시를 시작하고 `PlayerLeft(reconnectable=true)`를 보낸다 → 게임 에이전트가 세션을 `Reconnecting`으로 둔다(소유권 유지). 클라가 재접속하면 `ConnectionApproval`에서 티켓 없이 토큰만으로 복귀. 유예가 끝나면 `PlayerAbandoned` → 세션 삭제, 이후 재접속은 `PLAYER_ABANDONED`로 거부.
- **재접속 불가능한 방에서 클라가 끊김** — 즉시 탈락, `PlayerLeft(reconnectable=false)` → 게임 에이전트가 소유권을 즉시 놓음(`OwnerServerId = ""`, `OwnerServerType = Unspecified`). 클라는 로그인 서버를 거쳐 아무 로비로나 들어간다.
- **응접실 단계(방 시작 전)에서 끊김** — 응접실이 로스터에서 뺀다. 승격하거나 승격 없이 닫힐 때 탈락으로 알려 세션이 `Reconnecting`으로 남지 않게 한다.

### 인게임 → 로비 전환

**정상 흐름**: 게임 종료(`HandleMatchResultAsync`, 유니티가 보낸 `MatchResult`) 또는 방-채움 타임아웃(`RoomTimeout`) → `GameServerHandler.CloseRoomAndRedirectAsync` → `RequestLobbyAddressAsync`(매칭 서버한테 로비 주소 요청) → `IngameAgentMainServerIntermediary.IssueAndNotifyAsync`(전입 티켓 발급 + 로비의 `LobbyControlService.RegisterIncomingTransfers`에 미리 등록) → 세션을 `Moving`+`OwnerServerType.Lobby`로 갱신 → `CloseRoom`(티켓 포함 `PlayerRedirect`)을 유니티한테 전송 → `IngameServerInstance.OnCloseRoomReceived`가 대상 목록을 먼저 확정한 뒤 `RoomManager.CloseRoom`으로 매핑 정리 → `SendRoomClosedAndWaitAckAsync`로 클라한테 주소+티켓 전송, 확인 응답 대기 → `ClientInstance.OnRoomClosedMessageReceived`가 받아서 `_pendingTransferTicket`에 저장 → `ReconnectToLobbyAsync` → `LobbyClient.EnterLobbyAsync(ticket)` → `LobbyControlService.EnterLobby`가 토큰(`TokenVerification.IsValid`) + 전입 티켓(세션이 `Moving`이고 소유자가 이 로비일 때 `LobbyMainServerIntermediary.TryConsume`) 검증.

**발생 가능한 이벤트와 대응**:
- **`RoomClosed` 확인 응답이 안 옴(클라가 이미 끊김)** — `SendRoomClosedAndWaitAckAsync`가 `RoomClosedAckTimeout`(최대 5초) 뒤 그냥 진행, 어차피 방은 정리돼야 하므로.
- **확인 응답 대기 중 클라가 다른 이유로 먼저 끊김** — `IngameServerInstance.OnClientDisconnected`가 `RoomManager.GetRoomIdForPlayer`로 "이미 정리된 방(`RoomManager.CloseRoom`이 먼저 실행됨)"인지 확인, 맞으면 `PlayerLeft`를 게임 에이전트한테 아예 안 보냄(정상 종료로 인한 예상된 이탈이라는 걸 유니티가 로컬에서 이미 알고 있으므로 - 프로세스 경계를 안 넘음). 이 판정이 어긋나면(옛 버전) 게임 에이전트가 방금 정상적으로 이관한 세션을 "예기치 못한 이탈"로 오인해 덮어쓰는 버그가 있었음 - `KNOWN_ISSUES.md` 참고.
- **`RoomClosed` 메시지 자체를 클라가 못 받음(네트워크 완전 단절)** — 서버 쪽(세션, 티켓 등록)은 정리가 다 끝났는데 클라만 갈 곳을 모르는 상태로 남음. 클라가 로그인 서버로 재진입을 시도할 수밖에 없는데, **이 경로가 지금 막혀있음** - 아래 참고.

### 로그인 서버 = 갈 곳을 잃은 클라를 위한 안정적 단일 조회 창구 - 구현 완료

**설계 참고 사례**: AWS GameLift의 서버리스 매치메이킹 구조 - 클라는 특정 서버 주소를
직접 캐시/재시도하지 않고, 항상 안정적인 단일 창구(API Gateway)한테 "지금 상태 뭐야"를
물어본다. 그 창구가 내부적으로 상태 저장소(DynamoDB)를 조회해서 알려주는 구조. 우리
시스템에서 "안정적 단일 창구" 역할이 **로그인 서버**, "내부 상태 저장소" 역할이
**세션 레지스트리(Redis)**에 대응한다.

**흐름**:
1. 클라 접속이 끊김.
2. **재접속 가능한 방에서 순간적으로 끊긴 경우만 예외** - `RoomPolicy`(클라, 신규)가
   `ConnectionWelcome`으로 매번 받아둔 `allow_reconnect`를 보고 `ReconnectAction.Reconnect`를
   반환하면, 로그인 서버를 거치지 않고 기존에 갖고 있던 인게임 주소로 직접 재시도(빠른
   경로). 딱 한 번만 시도 - 재시도까지 실패하면 다음 끊김부턴 자동으로 3번 경로로
   폴백(`RoomPolicy`가 시도 이력을 캡슐화해서 무한 재시도를 막음).
3. 그 외 모든 경우(로비는 재접속 개념 자체가 없음 / 재접속 불가능한 방 / 2번 시도가
   실패) → 클라가 로그인 서버한테 `playerId` + 세션 토큰(계정 자격증명 재인증 아님)을
   제출(`ReconnectRequest`).
4. 로그인 서버가 세션 레지스트리 조회 - 살아있는 소유자가 있으면(`OwnerServerId` +
   그 서버의 하트비트 TTL) 토큰 대조 후 그 주소로 안내만 함(소유권 자체는 안 건드림).
   소유자가 없으면 완전히 새로 시작(기존 로직 그대로). `DestinationServerType`으로
   목적지가 로비인지 인게임인지 클라에 알려줌.
5. "목적지 서버가 죽었다"는 판단은 클라가 직접 접속 시도 실패로 알게 되는 것 - 로그인
   서버가 능동적으로 감지해서 push하는 게 아니라, 다음 조회 시점에 하트비트 TTL이
   자연스럽게 만료된 걸 보고 판단.

**번외 - "대참사" 시나리오(이동 중 도착지 서버가 죽거나 회전 토큰 2개 다 만료)**: 클라가
접속 거부당해 로그인 서버로 가도, 세션 레지스트리가 아직 그 서버를 "살아있다"고 보고
있을 수 있어(하트비트 TTL 만료 전) 새로 로그인해야 하는 상황이 될 수 있다. **이 경우
클라가 자동으로 즉시 재시도하는 게 오히려 안 좋다는 결론** - 서버 죽음은 TTL이 자연
만료될 시간이 필요하고, 토큰 2개 다 만료는 서버가 바쁘다는 신호라 즉시 재접속 폭주가
부하를 더 키운다. **자동 복구 로직 없이 "연결이 끊겼습니다"만 띄우고 사람이 직접
재시도하게 두는 게 나음** - 사람이 누르기까지의 지연이 TTL 만료 시간도 벌어주고 서버
부하도 자연스럽게 완화시킴. **백엔드 로직은 구현+빌드 확인 완료, 클라 UI만 미구현**
(`TODO.md` 참고).

---

## 보안

### 전송 구간 암호화 (TLS)
저장 시 해싱(아래 "비밀번호 저장" 참고)과는 완전히 다른 층위의 문제를 막는다 - 해싱이 "저장소가 뚫렸을 때 원문 노출"을 막는다면, TLS는 "네트워크 경로 어디든(공유기, ISP, 백본 등) 도청당했을 때 원문 노출"을 막는다. 둘 다 필요하고 서로 대체 관계가 아니다.

서버 7개(마스터, 로비, 게임 에이전트, 매칭, 계정, 로그인, 채팅) 전부 서버 간 통신을 포함해 TLS를 적용한다 - `Shared.Common.Hosting.KestrelEndpointExtensions.ConfigureGameServerEndpoints`가 `shared.appsettings.json`의 `"UseTls"` 값 하나로 전체를 일괄 통제한다(코드 재배포 없이 설정만으로 켜고 끔). 로컬 개발 중엔 `dotnet dev-certs https --trust`로 신뢰해둔 .NET 개발용 인증서를 서버들이 공유해서 쓴다. 실제 배포 시 정식 인증서(도메인 필요)로 교체하는 건 별도 작업으로 미뤄둠.

### 비밀번호 저장 - 해싱, 절대 원문 저장 안 함
`AccountServer`는 비밀번호를 원문(`AccountRecord.Password`)이 아니라 PBKDF2 해시(`AccountRecord.PasswordHash`)로만 저장한다(`Shared.Common.Auth.Pbkdf2PasswordHasher`, .NET 기본 클래스 라이브러리만 사용, 새 NuGet 패키지 없음). 해시는 되돌릴 수 없는 단방향 변환이라 서버조차 원문을 다시 알아낼 방법이 없다 - DB가 통째로 털려도 공격자가 얻는 건 해시값뿐이다. 검증도 원문 대조가 아니라 상수 시간 비교(`CryptographicOperations.FixedTimeEquals`)로 한다.

### ConnectionApproval
클라이언트가 게임 서버(NGO)에 접속할 때 `"{playerId}|{token}|{ticket}"` 페이로드를 포함한다. **1차로 유니티 서버(`IngameServerInstance`) 자신이 `RoomManager` 명단(`GetRoomIdForPlayer`)에 그 playerId가 있는지부터 확인한다** - 로비/매칭 서버가 `OpenRoom`으로 사전 등록해둔 명단에 없으면, 게임 에이전트한테 물어볼 것도 없이 즉시 거부한다(클라가 페이로드에 뭐라고 주장하든 검증 없이 믿지 않기 위함 - 진짜 신원 검증은 아래 2차가 함). 2차로 게임 에이전트가 Redis에서 세션을 조회해 상태(`Moving` 또는 `Reconnecting`), 소유자가 자신인지, 토큰 유효성(`TokenVerification.IsValid`), 전입 티켓(`Moving`일 때 필수)을 모두 검증하고 `TryClaimOwnershipAsync`로 원자적으로 확정한다. 게임 에이전트가 승인한 뒤에도 게임 서버의 방이 최종 판단한다(`Room.HandleApprovedConnection` - 탈락 확정자는 `PLAYER_ABANDONED`, 방이 정리 중이면 `ROOM_CLOSING` 등). 승인 시 새 토큰을 발급/회전하고 `ConnectionWelcome` 메시지(새 토큰 + 재접속 가능 여부, NGO 접속 완료 시 조건 없이 매번 전송)로 클라에 전달한다. 검증 실패 시 접속을 거부한다. → 자세한 경위는 [`KNOWN_ISSUES.md` #3](./KNOWN_ISSUES.md#3-connection-approval로비인게임에-토큰-검증이-아예-없었음) 참고.

### Moving / Reconnecting 상태
서버 이동 중인 플레이어는 Redis에 `Moving` 상태로 기록된다. 해커가 타인인 척 다른 서버에 접속하려 해도 Redis의 `OwnerServerId` 원자적 검증과 전입 티켓으로 차단된다. 재접속 대기 중인 플레이어는 `Reconnecting`으로 기록돼 티켓 없이 소유 서버로만 돌아갈 수 있다 - 두 상태를 나눈 이유는 "티켓이 필요한 이동"과 "티켓이 없는 재접속"을 티켓 존재 여부가 아니라 상태로 구분하기 위해서다(티켓은 TTL로 사라지므로 존재 여부로는 만료된 이동과 재접속이 구분되지 않았다).

### 토큰 (클라이언트 ↔ 로비/인게임 서버)
로비/인게임 서버와의 통신은 토큰으로 인증한다. `AuthInterceptor`(로비)/`GameServerHandler`(게임 에이전트)가 검증한다. `Login`/`CreateAccount`는 코드에서 제거됐고, 대신 로그인 서버(`LoginServer`)가 클라 최초 진입을 전담한다 - 계정 검증/생성(`AccountServer` 경유) 후 초기 토큰을 발급해 로비로 이관한다.

토큰은 `"{무작위값}:{playerId}"` 형식(`DummyTokenProvider`) - 서명은 없지만(진짜 JWT 라이브러리로 교체될 자리) 매 발급마다 값이 달라져서 2슬롯 대조가 실질적 효과를 갖는다. → 경위는 [`KNOWN_ISSUES.md` #2](./KNOWN_ISSUES.md#2-토큰이-playerid-그-자체라서-2슬롯-검증이-무의미했음) 참고.

세션 레지스트리(`PlayerSession`)에 `CurrentToken`/`PreviousToken` 2슬롯과 `TokenIssuedAtUnixMs`(TTL 판단용)를 둔다. 갱신 권한은 `OwnerServerId`를 가진 서버만 갖는다. 검증 지점은 이동 방향에 따라 다르다: **로비→인게임**은 게임 에이전트의 `ConnectionApproval`이, **인게임→로비**는 로비의 `EnterLobby`가 검증한다(둘 다 `TokenVerification.IsValid` 공용 함수 재사용). 채팅 서버는 이 회전에 관여하지 않는다 - 핸드셰이크 시점에 읽기 전용으로만 검증하고, 재연결 때마다 새로 검증할 뿐 자체적으로 토큰을 회전시키지 않는다("서비스 제공 가능 여부는 전적으로 메인 서버한테 맡긴다"는 원칙). → 경위는 [`KNOWN_ISSUES.md` #10](./KNOWN_ISSUES.md#10-websockethandler에-인증이-아예-없었음구버전-유산) 참고.

세션 자체엔 TTL이 없다(영구) - 개별 세션 생존 여부는 소유 서버의 `server:{serverId}` TTL로만 판단한다. → 경위는 [`KNOWN_ISSUES.md` #5](./KNOWN_ISSUES.md#5-세션-ttl24시간의-실제-역할을-서로-다르게-이해하고-있었음--결국-완전-제거) 참고.

### 서버 간 인증 (메인 서버 ↔ 마스터 서버)
`IServerAuthenticator` 퍼사드로 마스터 서버에 접속하는 서버를 검증한다. 지금은 `DummyServerAuthenticator`(항상 통과)이고, 나중에 mTLS를 도입할 때 여러 검증 수단을 조합하는 `CompositeServerAuthenticator`로 교체할 수 있게 인터페이스를 미리 분리해뒀다. `ConnectionHandler`가 접속 시작 시 가장 먼저 이 검증을 거치고, 실패하면 `Unauthenticated`로 즉시 연결을 끊는다.

### 클라가 로비에 있는 동안의 생존 감지 - `StayConnected`
로비는 클라와 지속 연결이 없어서(단항 RPC만 오감), 클라가 조용히 사라져도 감지할 방법이 예전엔 없었다. `StayConnected` 스트림을 EnterLobby 직후 열어서, 그 연결이 살아있다는 사실 자체로 생존을 판단한다. 매치 확정으로 정상 이동 중인 경우와 구분하기 위해 `OwnerServerId`+`Status`를 함께 확인한다. → 경위는 [`KNOWN_ISSUES.md` #4](./KNOWN_ISSUES.md#4-로비에-조용히-앉아있는-클라의-끊김을-감지할-방법이-없었음), [#6](./KNOWN_ISSUES.md#6-stayconnected-스트림-끊김과-매치-확정이-겹치는-race) 참고.

### 세션 무효화 전파 - `SessionInvalidated`
로비/게임 에이전트가 세션을 지우는 모든 지점(로그아웃, `StayConnected` 끊김, 재접속 유예 타임아웃)에서 RabbitMQ로 `SessionInvalidated{playerId}`를 발행한다. 채팅 서버가 구독해서, 이미 무효화된 세션의 웹소켓 연결을 강제 종료한다 - 채팅 서버는 이 신호로만 판단하고 스스로 세션 유효성을 재판단하지 않는다. → 경위는 [`KNOWN_ISSUES.md` #7](./KNOWN_ISSUES.md#7-sessioninvalidated-발행-소비-사이의-지연으로-생기는-race) 참고.

### 친구 삭제 → 채팅방 정리
`AccountServer.RemoveFriend`는 친구 목록을 갱신한 뒤 `RabbitMqBroker.PublishToQueue`로 `friend-removed` 이벤트를 발행한다 - 브로커가 연결이 없으면 쌓아뒀다가 접속되면 보내고 publisher confirm 뒤에만 지우므로, 예전의 별도 아웃박스(`FriendEventOutbox`/`FriendEventPublisher`)가 하던 일을 브로커가 대신한다. `ChatServer`는 수동 ack(처리 끝나야 ack, 실패 시 재큐잉)로 소비한다 - 둘이 합쳐 "최소 한 번은 처리된다"를 보장한다. 다만 버퍼는 메모리에만 있어서, 발행 전에 `AccountServer`가 죽으면 이벤트가 사라진다(예전 아웃박스도 인메모리라 같은 한계 - DB 전환 때 트랜잭션으로 묶을 것). → 경위는 [`KNOWN_ISSUES.md` #11](./KNOWN_ISSUES.md#11-accountserver가-rabbitmq-발행-중-죽으면-이벤트가-유실될-수-있는-문제-이중-쓰기-문제), [#12](./KNOWN_ISSUES.md#12-rabbitmqconsumer를-싱글톤으로-등록했더니-여러-구독이-서로의-채널을-덮어씀) 참고.

### 친구 관리 - 클라를 상대하는 메인 서버가 창구, 채팅 서버는 실행만
친구 관리(조회/추가/삭제)의 실제 데이터는 `AccountServer`가 갖고 있지만, 클라가 직접 그걸 호출하지 않는다. **지금 클라를 상대하는 메인 서버(로비 또는 인게임)가 대리 호출하는 창구 역할**을 한다 - "서비스 제공 가능 여부는 전적으로 메인 서버한테 맡긴다"는 원칙과 같은 결의 결정이다.

- **로비**: `lobby.proto`의 `GetFriends`/`AddFriend`/`RemoveFriend`를 클라가 직접 gRPC로 부르면, 로비가 `AccountServer`를 대리 호출한다.
- **인게임**: 클라가 NGO `CustomMessagingManager`로 "FriendCommand"를 보내면, 인게임 서버가 게임 에이전트에 릴레이(`ingame.proto`의 `FriendCommandRequest`/`FriendCommandResponse`, `GameServerMessage`/`GameAgentMessage`에 각각 포함)하고, 게임 에이전트가 `AccountServer`를 대리 호출한다. **클라가 요청에 자체 신고한 `player_id`는 신뢰하지 않는다** - 인게임 서버가 `ConnectionApproval` 때 이미 검증해둔 값으로 반드시 덮어써서 릴레이한다(아래 채팅 메시지 신뢰 판단과 같은 원칙).
- **예외** - `RequestChat`(대화 시작)은 "관리"가 아니라 클라가 이미 붙어있는 채팅 서버에 직접 요청한다. 채팅 서버가 내부적으로(읽기 전용) `AccountServer.GetFriends`를 불러 진짜 친구 사이인지 확인하는 건 이 원칙과 안 어긋난다 - 클라한테 그 기능을 노출하는 게 아니라 자기 방 생성을 검증하려고 남의 데이터를 읽어오는 것뿐이다.

### 채팅 메시지 신뢰 판단 - 타입으로 구분, 필드 값으로 안 함
`ChatMessageDelivery`(친구가 보낸 실제 메시지)와 `SystemChatDelivery`(채팅 서버가 직접 보내는 안내)는 완전히 다른 `oneof` 케이스다. "특정 필드가 비어있으면 신뢰할 메시지"같은 규약은 안 쓴다 - 클라가 정상 경로로 만들어낼 수 없는 타입이라는 사실 자체가 신뢰의 근거여야 한다. → 경위는 [`KNOWN_ISSUES.md` #9](./KNOWN_ISSUES.md#9-sender_id를-빈-값으로-시스템-메시지-특수-취급하려다-발견한-스푸핑-위험) 참고.

### ⚠ 알려진 경합 - 클라 자진 이탈 vs 서버 강제 종료 (게임 종료 후 로비 복귀)
게임 종료/방 취소 시, 인게임 서버는 클라에게 `CloseRoom`(리다이렉트 주소 포함)을 명시적 메시지로 보내고, 클라의 확인 응답을 기다린다. **클라는 확인 응답을 보낸 즉시(서버의 처리를 기다리지 않고) 스스로 NGO 연결을 끊고 새 로비로 이동을 시작**하고, **서버도 독립적으로(확인 응답을 받으면 곧장, 못 받으면 최대 5초 뒤) 자기 쪽에서 `DisconnectClient`를 호출**한다. 두 경로가 서로의 타이밍을 기다리지 않으므로, 아주 짧은 순간 "클라는 이미 끊었는데 서버는 아직 모르고 뒤늦게 `DisconnectClient`를 호출하려 하는" 경합이 생길 수 있다. 인게임 서버 쪽 `IngameServerInstance._clients`(`ConcurrentDictionary`)가 이 경합을 원자적으로 처리한다 - 서버가 끊으려는 시점에 그 클라가 이미 `_clients`에서 빠져있으면(클라가 먼저 끊어서 `OnClientDisconnected`가 이미 처리했으면) 그 시도를 무효로 돌린다. 실질적 위험(중복 처리, 잘못된 상태)은 없지만, 설계상 의도적으로 감수한 경합이라 기록해둔다.

### ⚠ 알려진 경합 - 친구 삭제 vs 메시지 전송
메시지 전송 시 방 권한을 확인하는 시점과 실제 전달/저장 시점 사이에 아주 짧은 틈이 있어, 그 틈에 친구가 삭제되면 메시지 한 통이 새어 들어올 수 있다. 서버는 별도 락을 안 걸고, 클라(`WebSocketChatChannel`)가 `RoomRemoved` 통보 이후의 메시지를 조용히 버리는 "차단 목록" 방식으로 창을 좁힌다. → 경위는 [`KNOWN_ISSUES.md` #8](./KNOWN_ISSUES.md#8-메시지-전송-권한-확인과-실제-전달저장-사이의-race-친구-삭제와-메시지-전송이-겹침) 참고.

전체 발견-원인-피해-해결-결과 상세 기록은 [`KNOWN_ISSUES.md`](./KNOWN_ISSUES.md) 참고.

## 우편함 (Mail)

`DummySubServer`(결제/보상/광고 등을 처리하는 백엔드 워커)가 처리한 결과를 클라에게 어떻게 전달할지 논의하다가 확립된 시스템. **핵심 원칙: "실제 상태 변경"과 "그 알림"을 분리한다** - 실제 상태 변경(보상 지급 등)은 `AccountServer`에 영속적으로 먼저 기록되고, 알림은 best-effort다(놓쳐도 클라가 나중에 재조회로 만회 가능). 업계 표준 "메일함/인박스" 패턴(Heroic Labs, Unity, LootLocker 등이 쓰는 것과 같은 개념)을 따른다.

**왜 채팅 서버가 아니라 `AccountServer`가 담당하는가**: 메일 "개봉"은 단순 열람이 아니라 보상 지급이 같이 일어나는 경제적 트랜잭션이다. 지급과 열람 시점이 어긋나면(캐시성 보상을 미리 지급해두고 메시지만 나중에 보내는 식) CS 분쟁으로 이어질 수 있어서, "수령"은 계정 데이터를 원자적으로 다루는 `AccountServer`가 책임진다.

### 전체 흐름
```
1. 메인 서버 → 하위 서버: RabbitMQ로 지시 하달 (reward-queue 등 기존 큐 재사용)
2. 하위 서버(DummySubServer.MailGranter) → AccountServer.GrantMail
   - source_event_id로 멱등성 보장(RabbitMQ가 "최소 한 번" 배달이라 재시도로 중복 지급될 수 있음)
3. AccountServer가 영속 저장 후 적용 완료 통지
4. 하위 서버가 세션 레지스트리(ISessionRegistry.GetOwnerAsync)로 그 플레이어를 지금 누가 관리 중인지 조회
5. 있으면(온라인이면) 그 서버(로비 or 게임 에이전트, OwnerInfo.ServerType으로 구분)한테
   "우편물 제공" 신호만 전달(내용은 안 실음) - 없으면 아무것도 안 함
6. 신호 받은 서버가 AccountServer.GetMailList를 직접 조회해서 그 즉시 클라한테 전달
7. 모든 메인 서버는 클라 접속 시점에도 항상 자동으로 우편함을 제공한다(공통 규약) -
   4~6번이 어떤 이유로든 실패해도(하위 서버 다운 등) 이게 최종 안전망
8. 클라가 메일 클릭 → 확인 → mail_id로 AccountServer.OpenMail 요청 →
   그 순간 재검증(만료/중복수령 여부) 후 원자적으로 수령 처리 + 보상 지급
```

**로비**: `StayConnected` 스트림(이미 있던 생존 감지 채널)을 그대로 재사용한다 - `NotifyMailUpdated` RPC가 오면 그 플레이어의 채널(`Channel<StayConnectedEvent>`, playerId별로 관리)에 `MailProvided`를 넣고, `StayConnected` 핸들러가 그 채널을 순차적으로 읽어서 스트림에 쓴다(동시 쓰기 방지 - 여러 곳에서 같은 스트림에 직접 쓰면 깨질 수 있어서, 쓰기는 이 핸들러 하나만 담당).

**게임 에이전트**: `GameServerInfo.GetClientId(playerId)`로 지금 이 게임 에이전트가 담당하는 게임 서버에 그 플레이어가 접속 중인지 확인 후, `GameAgentMessage.MailProvided`로 게임 서버(Unity)에 릴레이한다. Unity `IngameServerInstance`가 `PlayerId`로 `clientId`를 역탐색(선형 탐색 - 방 하나당 인원이 적어 문제없음)해서 NGO `CustomMessagingManager`로 실제 클라에게 전달한다.

**`OwnerInfo.ServerType`/`PlayerSession.OwnerServerType`**: 하위 서버가 주소만으로는 "로비인지 게임 에이전트인지" 구분할 수 없어서 추가했다. `TryClaimOwnershipAsync` 계열 메서드 시그니처는 안 건드리고, 호출부(`LobbyControlService.EnterLobby`, `GameServerHandler.HandleApprovalRequestAsync`)가 세션 객체에 미리 채워서 넘기는 방식으로 파급 범위를 최소화했다.

### mail_id 기반 식별 - 클라 UI 안정성의 핵심
목록이 백그라운드에서 갱신돼도(새 메일 도착 등) 기존에 뜬 확인창/버튼이 가리키는 메일이 안 바뀌어야 한다 - "몇 번째 항목" 같은 위치 기반이 아니라 `mail_id`(안정적 식별자)로 항상 참조한다. 클릭 핸들러도 객체 참조가 아니라 `mail_id` 문자열만 캡처해야 한다. 클라 내부 자료구조는 `Dictionary<mailId, MailInfo>`가 진짜 데이터이고, UI에 보이는 "날짜순 정렬된 목록"은 그 딕셔너리에서 매번 파생시키는 뷰로 다뤄야 한다(별도로 유지관리하는 정렬 리스트를 따로 안 둠). 서버 측 재검증(`OpenMail`)과 합쳐지면, 어떤 타이밍에 뭐가 어긋나도 클라한테 애매한 에러 없이 명확한 사유(`ALREADY_CLAIMED`/`EXPIRED`/`NOT_FOUND`)로 처리된다.

### 우편함 상한선 + 밀어내기 정책
`mail_type`은 자유 문자열이 아니라 `common.proto`의 `MailType` enum이다 - `ItemProvision`(보상, 점검/장애/이벤트 등 세부 사유는 전부 통합)과 `GuildInvite`(수락이 실제 결정이라 이득이 불분명한 예시) 두 갈래로, 어떤 타입이 "받아서 손해 없는 것"인지 판단하는 기준이 된다. `MailboxEvictor`(`AccountServer`)가 우편함이 `AccountServerConfig.MailboxCapacity`를 넘으면 밀어낸다 - 순서는 **수령됨 > 만료됨 > 오래된 것**(정렬 하나로 세 우선순위를 한 번에 표현: `OrderByDescending(Claimed).ThenByDescending(만료여부).ThenBy(생성시각)`). 아직 미수령 상태로 밀려나야 하는 우편은 `ItemProvision`이면 밀려나기 전에 자동 수령 처리(`TryClaimAsync`) 후 삭제, 그 외 타입은 그냥 삭제(거부)한다. 발동 시점은 우편 생성 시점이 아니라 클라의 `GetMailList` 요청 시점 - "만들 때마다 확인"이 아니라 "조회할 때 정리"로, 확인 빈도를 자연스럽게 줄인다.

### ⚠⚠⚠ 클라이언트(Unity) UI 연동은 서버 배선과 별개로 신중히 다룰 것
서버 측(로비/게임 에이전트/`AccountServer`/`DummySubServer`, Unity의 `IngameServerInstance`까지)은 전부 완료됐다. **`ClientInstance.cs`(실제 클라이언트)의 수신 처리와 UI(메일함 화면, 클릭→확인→개봉 흐름, 알림음/배지)는 의도적으로 손 안 댔다** - 이건 단순 배선이 아니라 실제 게임 UI와 밀접하게 엮인 작업이라, 위 `mail_id` 기반 식별 원칙을 UI 코드가 정확히 안 지키면 "확인창 떠있는 동안 배경이 갱신되는" 류의 race가 실제로 터질 수 있다. UI 설계와 반드시 같이 논의해서 진행할 것.

## Unity 클라/서버 스레드 안전성 - MainThreadDispatcher

### 왜 필요한가
유니티는 공식적으로 `await` 재개를 메인 스레드로 되돌려주지만(`SynchronizationContext`), 여기 의존하는 것만으로는 부족하다는 근거를 찾았다: (1) 유니티 자신도 네트워킹처럼 지연이 우려되는 경우 커스텀 동기화를 권장하고, (2) `SynchronizationContext` 캡처 자체에 성능 비용이 있으며, (3) 결정적으로 - **유니티는 성능상의 이유로 실제 출시(비개발) 빌드에서는 멀티스레드 오작동 검사/에러 표시를 하지 않는다**. `ConfigureAwait(false)`가 실수로 들어가도 배포 빌드에서 조용히 터질 수 있다는 뜻이다.

### 설계
백그라운드 스레드(gRPC 수신 루프 등)는 `Action`(로직)이 아니라 **타입 있는 데이터만** 큐에 넣는다 - "무슨 일이 있었는지"만 알려주고 "뭘 할지"는 절대 안 넘긴다. `object` + `is` 타입 검사도 안 쓴다(캐스팅 비용) - 대신 proto `oneof`(`PayloadCase`, 정수 비교)로 타입을 구분한다. `Dispatch`는 "브로드캐스트 후 각자 확인"이 아니라 **한 곳(스위치)에서 정확히 어느 함수를 부를지 결정**하고, 실제 처리 로직만 기능 영역별 `partial` 파일로 나눈다. 큐 자체는 `ConcurrentQueue`, 정적 클래스 + `[RuntimeInitializeOnLoadMethod]`로 씬에 아무것도 안 놓고 재생 시점에 스스로 드라이버(`MonoBehaviour`)를 생성한다.

클라/서버(`IngameServerInstance`)는 **완전히 분리된 별도 proto 프로젝트 + 클래스명**이다 - 배포 시 바이너리가 나뉘므로, 합치면 서버 내부 이벤트 구조를 클라 빌드에서 추측할 여지를 준다고 판단했다. `ClientDispatch.Contracts`(`ClientDispatchEvent`, `ClientMainThreadDispatcher`) / `IngameServerDispatch.Contracts`(`IngameServerDispatchEvent`, `IngameServerMainThreadDispatcher`). 둘 다 gRPC 서비스 없이 순수 메시지만(`GrpcServices="None"`).

### 소비자 선택 기준
게임 로직 판단이 필요한 이벤트(`OpenRoom`처럼 `RoomManager` 등에 위임되는 것)는 `GameManager`가 소비하고, 순수 네트워크 전달/상태 갱신인 이벤트(`CloseRoom`, 친구 관리, 공지 등 - `CloseRoom`은 겉보기와 다르게 "판단"이 아니라 NGO 클라 정리의 전제 조건으로 `RoomManager.CloseRoom`을 부르는 것뿐)는 `IngameServerInstance`가 직접 소비한다 - 이미 있던 `IngameServerInstance ↔ GameManager ↔ CompleteWork` 왕복 패턴과 일치시켰다.

예전부터 `IngameServerInstance.cs` 안에 `_workResults`(`ConcurrentQueue<ClientWorkResult>`)라는 자체 큐가 파묻혀 있었는데, 이 디스패처와 정확히 같은 목적으로 예전부터 독립적으로 존재했던 걸 발견해서 완전히 통합했다(`Update()`의 수동 소비 루프 제거).

## 공지 시스템

관리자가 "N분 뒤 서버가 종료됩니다" 같은 공지를 전체 클라(또는 특정 서버 타입/인스턴스)한테 방송한다. 업계 사례(AccelByte의 notification manager, Velocity 프록시의 status-service) 조사 결과 "중앙 창구 하나가 공지의 유일한 소스가 되고, 그게 연결된 모든 서버한테 브로드캐스트한다"는 패턴이 공통적이었다 - 우리 시스템에서 그 중앙 창구가 **마스터 서버**(이미 `MasterServerInstruction`으로 이 역할을 하고 있었으므로).

**패킷 절감**: 자유 문구 대신 `AnnouncementType`(enum) 값 하나로 퉁긴다 - 클라가 그 값을 받아서 자기 언어/문구로 알아서 해석한다. 전체 브로드캐스트 경로에서 패킷 양이 상당할 수 있어서, `/admin/announce`부터 최종 클라 수신까지 문자열이 오가는 지점이 하나도 없다.

**흐름**: `/admin/announce`(관리자) → `MasterServerInstruction.Announcement`(`AnnouncementTask{ announcement_type, active }` - 지속 업무라 `active`로 시작/중지 구분) → 로비/게임 에이전트(둘 다 `MasterConnectionService.OnAnnouncementRequested` 구독) → 각 서버가 자기 클라들한테 릴레이.

**로비(진짜 멀티스레드, ASP.NET Core)**: `GlobalWorker`(신규, `BackgroundService` - `AddSingleton`+`AddHostedService`로 단일 인스턴스+서버 시작 즉시 실행 보장)가 `Works`/`PendingWorks`(순서대로 처리) + `_standingWorkItems`(지금 활성화된 지속 업무 - 새로 접속하는 클라한테 한 번에 다 주기 위한 소스)를 둔다. **경합 방지**: "클라 접속 등록"과 "공지 활성화 시 전체 스냅샷"이 겹치면 그 찰나에 접속한 클라가 공지를 영영 못 받을 수 있어서, `LobbyConnectionRegistry.SyncRoot`(노출된 락 - 접속자 명단과 관련된 상태를 조작하려면 반드시 이 락을 잡아야 함) 안에서 등록+활성화가 항상 순서대로 원자적으로 일어나게 한다(중첩 순서: SyncRoot → GlobalWorker 내부 락, 항상 이 순서 고정 - 데드락 방지).

**게임 에이전트(1:1 연결, `GameServerHandler`가 `AddSingleton`)**: "모든 게임 서버 인스턴스한테 뿌린다"는 개념 자체가 필요 없다 - 그냥 이 하나의 연결(`OutboundQueue`)한테 마스터 지시를 그대로 릴레이만 하면 된다.

**인게임 서버(유니티, 단일 스레드)**: 로비와 다르게 **락이 전혀 필요 없다** - `OnClientConnectedCallback`도, 디스패처가 큐를 비우는 것도 전부 메인 스레드 하나에서만 실행되므로, 처리 함수 안에 `await` 없이 동기적으로 "등록+스냅샷 방송"을 끝내기만 하면 그 사이에 다른 코드가 절대 끼어들 수 없다. `OnClientReady`(신규, `IngameServerInstance` - NGO 접속이 실제로 완료된(`ConnectionWelcome`까지 전달된) 클라를 알려줌, `ReadyClientInfo`로 감싸서 나중에 필드가 늘어나도 델리게이트 시그니처가 안 바뀌게 함)에 공지 전달 함수를 등록해서 새 접속자한테 계속 전달하고, 지금 접속 중인 전원한테는 `CustomMessagingManager.SendNamedMessage`의 **다중 클라 오버로드**(`IReadOnlyList<ulong>` 받는 것)로 반복문 없이 한 번에 방송한다.

## 테스트 하네스 연동

자동 E2E 테스트는 별도 저장소 `TestHarness`가 담당한다(절차는 `TestHarness/README.md`). 서버 쪽에는 테스트 전용 코드만 따로 둔다 - 실제 코드(`Shared.Common` 등)와 섞지 않는다.

- **위치**: `Server/GameServer/Shared/Test/` - `Harness.Contracts`(proto 원본은 TestHarness에 있고 빌드 때 복사됨, 생성된 C#은 Unity `Assets/Scripts/Test/Harness/Generated`로 다시 복사), `Harness.Agent`(서버 쪽 에이전트).
- **동작**: 환경 변수 `HARNESS_ADDRESS`가 있을 때만 `AddHarnessAgent`가 켜진다(없으면 아무것도 안 함). 켜지면 각 서버가 하네스에 gRPC 양방향 스트림(`HarnessControl.Attach`)으로 붙어, 하네스가 `[HarnessCommand("영역.동작")]`으로 등록된 디버그 명령을 원격 호출하고 이벤트를 받는다. 연결이 끊기면 1초마다 다시 붙는다.
- **로그**: 하네스 모드로 띄우면(`DevTools/start-servers.ps1 -LogDir`) 서버 콘솔 출력이 JSON 형식으로 파일에 남고, 판정은 하네스가 한다 - 서버는 "무엇이 일어났는지"를 구체적인 값으로 로그에 남기기만 한다.

## 코딩 관례

### fire-and-forget 함수는 반드시 함수 전체를 try-catch로 감쌀 것
`_ = SomeAsync(...)`처럼 아무도 결과를 기다리지 않는(await 안 하는) 함수 안에서 예외가 새면, 그 예외는 아무도 관측하지 못한 채 조용히 사라진다 - 특히 `Task.Delay`, 스트림 반복(`await foreach`) 등 함수 중간의 `await` 지점에서 취소/예외가 나면 함수가 아무 로그도 없이 그냥 멈춰버릴 수 있다. **부분적으로만("이 부분은 위험해 보이니까") 감싸지 말고, 함수 진입 시점부터 끝까지 전체를 감쌀 것** - 정확히 어느 지점이 위험한지 매번 판단하는 것보다, 통째로 감싸는 게 더 안전하고 유지보수하기도 쉽다.

반대로 **호출자가 직접 `await`해서 결과/예외를 받는 함수**(`RequestChatAsync` 등 요청-응답 메서드)는 내부에서 예외를 삼키면 안 된다 - 호출자가 취소/실패 여부를 알아야 하므로, 그 판단은 호출자에게 맡긴다.

대상 예시: `WebSocketChatChannel.RunWithReconnectAsync`, `GrpcLobbyChannel.StayConnectedAsync`, `GrpcIngameServerChannel.SendLoopAsync`, `ClientInstance.ReconnectToLobbyAsync`, `IngameServerInstance.SendRoomClosedAndWaitAckAsync` 등 - 전부 이 원칙에 따라 점검 완료.
