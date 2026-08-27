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
              └── Redis: 세션 업데이트, ConnectionApproval 검증 (OwnerServerId 기반)

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
  └── [예정] Redis Sorted Set 기반 실력 점수 매칭 - 지금은 더미(1명이면 즉시 매칭)

백업 서버 (.NET 8) [예정]
  └── 마스터 서버를 제외한 다른 서버가 죽었을 때 대신 업무를 이어받음

하위 서버들 (.NET 8)
  ├── 보상 서버: RabbitMQ work queue 소비 [예정]
  ├── 결제 서버: RabbitMQ work queue 소비 [예정]
  └── 더미 서버: 개발용 (DummySubServer)

브로커
  ├── Kafka: 매치 결과, 게임 이벤트 (분석/비동기) [예정 - 아직 아무도 발행 안 함]
  ├── RabbitMQ: 보상/결제 (work queue), 운영 공지 (fanout) [예정]
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
- **재접속 정책**: 재접속 허용 여부는 방 단위로 결정한다. 싱글플레이는 방 1개당 유저 1명이라 사실상 플레이어 단위와 동일하다. 세션 레지스트리 TTL(서버 생존)과 `TryClaimOwnershipWithPreviousOwnerAsync`로 재접속 시 권한 이전을 처리한다. 게임 중 재접속 유예 시간 안에 실패하면 세션을 완전 삭제한다(재로그인 필요). (상세 트리거/타이머는 상세 구현 예정 - `TODO.md` 참고)
- **ClientInstance**: `DontDestroyOnLoad` 싱글톤. 씬 전환 시에도 유지되며 `LobbyClient`, `ChatClient`, 추후 `IngameClient`를 들고 있다. 언리얼의 `GameInstance`에 대응하는 개념.

### 마스터 서버

모든 메인 서버(로비, 게임 에이전트, 채팅, 매칭)의 허브 역할을 한다.

- 각 서버 인스턴스의 부하/수용량을 추적하는 인스턴스 레지스트리를 관리한다.
- **접속 서버 인증**: `IServerAuthenticator` 퍼사드로 접속하는 서버를 검증한다. 지금은 `DummyServerAuthenticator`(항상 통과)이고, 나중에 mTLS 도입 시 여러 검증기를 조합하는 `CompositeServerAuthenticator`로 교체할 수 있게 설계했다.
- **매칭 서버 주소 배포**: 메인 서버가 최초 접속하면(기동 순서는 마스터→매칭→메인) 이미 등록된 매칭 서버 주소를 즉시 전달한다. 매칭 서버가 재등록되면(재시작 등) 접속 중인 메인 서버 전체에 브로드캐스트한다.
- **세션 레지스트리(Redis) 주소 배포**: 마스터가 `shared.appsettings.json`에서 읽은 Redis 주소를 접속하는 모든 서버 타입(매칭 서버 포함)에 최초 등록 시 바로 전달한다. 메인 서버들은 더 이상 Redis 주소를 로컬 설정에서 직접 읽지 않는다.
- 드레인 신호로 특정 서버의 신규 접속을 차단한다.
- 매칭/채팅 등 비즈니스 로직은 담당하지 않는다.
- (레거시) `RequestIngameAssignment` RPC가 남아있으나 매칭 서버 도입 이후 아무도 호출하지 않는다 - 정리 대상.

### 로비 서버

클라이언트가 로비 상태에서 접속하는 서버다.

- 클라이언트와 gRPC로 통신한다. `Login`/`CreateAccount`(순수 인증), `EnterLobby`(권한 획득), `Logout`, `RequestMatch`, `CancelMatch`를 처리한다.
- JWT 기반 인증을 담당한다. `AuthInterceptor`가 모든 요청(Login/CreateAccount 제외)에서 토큰을 자동 검증한다.
- `EnterLobby`에서 세션 레지스트리에 관리 권한을 요청한다(`TryClaimOwnershipWithPreviousOwnerAsync`) - 다른 서버가 아직 살아서 소유 중이면 거부되고 그 주소를 클라에 돌려준다.
- `RequestMatch` 시 매칭 서버에 `JoinMatch` 요청 후, 매칭 서버가 접속 유지 스트림으로 push하는 `MatchNotification`을 `MatchNotificationDispatcher`가 받아서 대기 중인 클라 스트림으로 전달한다.
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
- 게임 에이전트와의 연결이 끊기면(하트비트 타임아웃 포함) `GameManager.Shutdown()`이 `ManagerType` 순서(Room→Network)대로 전부 정리 후 프로세스를 종료한다 - 되돌릴 수 없는 절차로 확정했다("게임 에이전트를 되살려서 재연결"하는 경로는 TODO로 보류).

### 게임 에이전트

게임 서버(Unity)의 사이드카 역할을 한다. Unity에서 직접 처리하기 어려운 서버 로직을 담당한다.

- 게임 서버와 gRPC 양방향 스트림으로 연결된다.
- `ConnectionApproval` 검증을 담당한다. Redis에서 세션을 조회해 `Moving` 상태와 `OwnerServerId`를 확인한다(`TryClaimOwnershipAsync`로 원자적 이전). `MatchToken` 검증은 폐지됨.
- 플레이어 접속/퇴장 시 Redis Session Registry를 업데이트한다.
- 마스터 서버에 게임 서버의 부하/수용량을 보고한다.
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
- [예정] Redis Sorted Set으로 실력 점수 기반 매칭 대기열을 관리한다. 지금은 더미(대기자 1명이면 즉시 매칭).
- [예정] 매칭 수락 화면 지원.

### 백업 서버 [예정]

마스터 서버를 제외한 다른 서버가 죽었을 때 대신 업무를 이어받는다. 상세 설계는 추후 논의.

### Redis Session Registry

플레이어와 서버의 "관리 권한 기록부"다. 방 상태 같은 각 서버의 업무 데이터는 여기 두지 않는다(각 서버 로컬 메모리에서 관리 - 예: 매칭 서버의 `_matches`, 인게임 서버의 `RoomManager`. 방은 그 방을 연 서버와 운명을 같이 하므로 외부에 복제해봤자 의미가 없다는 판단).

- 모든 서버가 동일한 Redis를 바라봐서 어느 서버든 플레이어의 현재 상태를 조회할 수 있다.
- **주소는 정적 설정이 아니라 마스터가 동적으로 배포한다.** 접속하는 서버는 `ISessionRegistryConnector`(→ 실제 구현 `SessionRegistryConnector`)로 주소를 받을 때까지 대기했다가 연결한다 - 그 전에 `ISessionRegistry`를 호출하는 코드는 자동으로 대기 상태가 된다.
- 플레이어 상태: `Lobby → Matching → Moving → Ingame → Moving → Lobby`
- `Moving` 상태는 서버 이동 중임을 나타낸다. 이 시점에 `NextServer`가 저장된다(예전엔 일회용 `MatchToken`도 저장했으나 폐지 - `OwnerServerId` 기반 검증으로 대체).
- **서버 등록/하트비트 분리**: 서버 기동 시 `RegisterServerAsync(id, address)`로 ID+주소를 한 번만 등록하고, 이후 `RefreshServerHeartbeatAsync`는 `EXPIRE`로 TTL만 갱신한다(값 재전송 없음 - 하트비트 부하 최소화). 하트비트 간격 5초 / TTL 10초.
- **소유권 이전**: `TryClaimOwnershipAsync`/`TryClaimOwnershipWithPreviousOwnerAsync`(Lua 스크립트로 원자적 처리). 세션이 없으면 무조건 승인(최초 로그인), 기존 소유자가 죽어있으면 승인, 살아있으면 거부(+ 후자는 기존 소유자 주소를 같이 반환해서 `EnterLobby`가 "진행 중인 게임이 있습니다" 안내에 쓸 수 있게 함).
- TTL로 클라이언트 크래시 시 세션을 자동 만료한다. 재접속 유예 시간 안에 실패하면 세션을 `RemoveAsync`로 완전 삭제한다(재로그인 필요).

### 브로커

**Kafka**: 매치 결과, 게임 이벤트, 채팅 로그 등 분석/집계가 필요한 대용량 이벤트를 처리한다. 수십ms 지연이 있어 매칭같은 실시간 처리에는 부적합하다. (아직 아무도 실제로 발행하지 않음 - 프로듀서 유틸만 준비돼 있음)

**RabbitMQ**: 보상 지급, 결제 처리, 신고 처리 등 정확히 한 번 처리가 보장돼야 하는 작업에 쓴다. Work queue 패턴으로 하위 서버들이 소비한다. Fanout exchange로 운영 공지를 브로드캐스트한다. (하위 서버 자체가 아직 더미만 있음)

**Redis**: Session Registry(관리 권한 기록부), 매칭 대기열(예정), 채팅 Pub/Sub에 쓴다. 인메모리라 응답이 빠르고 TTL로 자동 만료가 가능하다.

---

## 보안

### ConnectionApproval
클라이언트가 게임 서버(NGO)에 접속할 때 `playerId` 페이로드를 포함한다(예전엔 `matchToken`도 같이 보냈으나 폐지). 게임 에이전트가 Redis에서 세션을 조회해 `Moving` 상태와 `OwnerServerId` 소유권 이전 여부(`TryClaimOwnershipAsync`)를 검증한다. 검증 실패 시 접속을 거부한다.

### Moving 상태
서버 이동 중인 플레이어는 Redis에 `Moving` 상태로 기록된다. 해커가 타인인 척 다른 서버에 접속하려 해도 Redis의 `OwnerServerId` 원자적 검증으로 차단된다 (예전엔 `NextServer` + 일회용 `MatchToken` 조합이었으나, 매칭 서버가 게임 에이전트에 플레이어 명단을 직접 전달하는 구조로 바뀌면서 토큰이 불필요해졌다).

### JWT (클라이언트 ↔ 로비 서버)
로비 서버와의 통신은 토큰으로 인증한다. `AuthInterceptor`가 모든 gRPC 요청에서 토큰을 자동 검증한다. `Login`/`CreateAccount`는 코드에서 제거됐다(별도 "로그인 서버"로 분리하는 게 맞는 방향이지만 나중 작업 - 지금은 `EnterLobby`부터 토큰이 필요한 임시 상태). 클라는 최초 접속 시 스스로 만든(사실상 playerId 그대로인) 토큰을 제출하고, `EnterLobby`가 성공하면 그 자리에서 `ITokenProvider.GenerateToken`으로 새 토큰을 발급해 돌려준다. `DummyTokenProvider`는 지금 어떤 문자열이든 그대로 playerId로 취급해 통과시키므로 실질적인 신원 검증이 없는 상태 - 로그인 서버가 생기기 전까지의 임시 조치.

세션 레지스트리(`PlayerSession`)에 `CurrentToken`/`PreviousToken` 2슬롯과 `TokenIssuedAtUnixMs`(TTL 판단용)를 둔다. 갱신 권한은 `OwnerServerId`를 가진 서버만 갖는다 - 즉 지금 그 플레이어를 관리 중인 서버만 토큰을 새로 발급할 수 있다. 검증 지점은 이동 방향에 따라 다르다: **로비→인게임**은 게임 에이전트의 `ConnectionApproval`이 검증하고(서버 간 소통을 게임 에이전트가 대신하므로), **인게임→로비**는 로비의 `EnterLobby`가 검증한다. 실제 "제출된 토큰을 세션의 2슬롯과 대조해서 거부"하는 로직 자체는 아직 미구현(TODO) - 지금은 `EnterLobby`가 무조건 새로 발급하고, `ConnectionApproval`은 `OwnerServerId`만 보고 토큰은 안 본다.

### 서버 간 인증 (메인 서버 ↔ 마스터 서버)
`IServerAuthenticator` 퍼사드로 마스터 서버에 접속하는 서버를 검증한다. 지금은 `DummyServerAuthenticator`(항상 통과)이고, 나중에 mTLS를 도입할 때 여러 검증 수단을 조합하는 `CompositeServerAuthenticator`로 교체할 수 있게 인터페이스를 미리 분리해뒀다. `ConnectionHandler`가 접속 시작 시 가장 먼저 이 검증을 거치고, 실패하면 `Unauthenticated`로 즉시 연결을 끊는다.

### ⚠ 알려진 경합 - 클라 자진 이탈 vs 서버 강제 종료 (게임 종료 후 로비 복귀)
게임 종료/방 취소 시, 인게임 서버는 클라에게 `CloseRoom`(리다이렉트 주소 포함)을 명시적 메시지로 보내고, 클라의 확인 응답을 기다린다. **클라는 확인 응답을 보낸 즉시(서버의 처리를 기다리지 않고) 스스로 NGO 연결을 끊고 새 로비로 이동을 시작**하고, **서버도 독립적으로(확인 응답을 받으면 곧장, 못 받으면 최대 5초 뒤) 자기 쪽에서 `DisconnectClient`를 호출**한다. 두 경로가 서로의 타이밍을 기다리지 않으므로, 아주 짧은 순간 "클라는 이미 끊었는데 서버는 아직 모르고 뒤늦게 `DisconnectClient`를 호출하려 하는" 경합이 생길 수 있다. 인게임 서버 쪽 `IngameServerInstance._clients`(`ConcurrentDictionary`)가 이 경합을 원자적으로 처리한다 - 서버가 끊으려는 시점에 그 클라가 이미 `_clients`에서 빠져있으면(클라가 먼저 끊어서 `OnClientDisconnected`가 이미 처리했으면) 그 시도를 무효로 돌린다. 실질적 위험(중복 처리, 잘못된 상태)은 없지만, 설계상 의도적으로 감수한 경합이라 기록해둔다.
