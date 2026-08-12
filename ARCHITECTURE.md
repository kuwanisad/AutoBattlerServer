# 아키텍처 (ARCHITECTURE)

---

## 설계 철학

**단일 책임 원칙**
각 서버는 하나의 역할만 담당한다. 마스터 서버가 매칭까지 담당하거나, 로비 서버가 채팅까지 담당하는 식의 비대화를 지양한다. 기능이 추가될 때 기존 서버를 수정하는 대신 새 서버를 추가하는 방향을 선호한다.

**인터페이스 격리**
통신 수단에 직접 의존하지 않는다. `IIngameServerChannel`, `ILobbyChannel`, `IChatChannel` 같은 인터페이스로 구현체를 격리해서, 나중에 통신 방식이 바뀌어도 비즈니스 로직은 수정하지 않아도 된다.

**현업 관행 우선**
설계 결정이 필요할 때 현업에서 검증된 패턴을 먼저 찾는다. 매칭 대기열에 Redis Sorted Set, 서버 간 메시지 동기화에 Redis Pub/Sub, 인증에 JWT, 채팅에 WebSocket + Protobuf를 쓰는 것이 그 예다.

**점진적 구현**
더미 구현체(DummyTokenProvider, DummyLobbyChannel, DummyChatChannel 등)로 껍데기를 먼저 완성하고, 실제 구현체로 교체하는 방식을 취한다. 인터페이스가 안정적이면 구현체 교체가 다른 코드에 영향을 주지 않는다.

**상태의 단일 진실 공급원**
플레이어 상태(로비/매칭/이동중/인게임)는 Redis Session Registry가 단독으로 관리한다. 어느 서버든 Redis를 조회하면 현재 플레이어의 정확한 상태를 알 수 있다. 이를 통해 서버 이동 시 발생할 수 있는 보안 취약점(타인 사칭)을 차단한다.

---

## 구조도

```
클라이언트 (Unity)
  │
  ├── gRPC (로비 상태)
  │     ↓
  │   로비 서버 (.NET 8, 5200/5201)
  │     ├── 마스터 서버와 gRPC 연결 유지
  │     └── Redis: 세션 등록/업데이트
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
        │
        └── gRPC (7000/7001)
              ↓
            게임 에이전트 (.NET 8)
              ├── 마스터 서버와 gRPC 연결 유지
              └── Redis: 세션 업데이트, ConnectionApproval 검증

마스터 서버 (.NET 8, 5100/5101)
  ├── 모든 메인 서버(로비/게임 에이전트/채팅)와 gRPC 연결 유지
  └── 인스턴스 레지스트리 관리

매칭 서버 (.NET 8) [예정]
  ├── Redis: 매칭 대기열 (Sorted Set)
  └── 로비 서버에 매치 결과 통보

백업 서버 (.NET 8) [예정]
  └── 마스터 서버를 제외한 다른 서버가 죽었을 때 대신 업무를 이어받음

하위 서버들 (.NET 8)
  ├── 보상 서버: RabbitMQ work queue 소비
  ├── 결제 서버: RabbitMQ work queue 소비
  └── 더미 서버: 개발용

브로커
  ├── Kafka: 매치 결과, 게임 이벤트 (분석/비동기)
  ├── RabbitMQ: 보상/결제 (work queue), 운영 공지 (fanout)
  └── Redis: Session Registry, 매칭 대기열, 채팅 Pub/Sub
```

---

## 각 요소의 기능

### 클라이언트 (Unity)

로비 상태와 인게임 상태 두 가지로 존재하며, 채팅 서버와는 항상 연결을 유지한다.

- **로비 상태**: gRPC로 로비 서버와 통신한다. 로그인, 매칭 요청, 채팅 등을 처리한다.
- **인게임 상태**: NGO(Unity Transport)로 게임 서버와 통신한다. 실시간 게임 동기화를 담당한다.
- **채팅**: WebSocket + Protobuf으로 채팅 서버와 로그인~로그아웃 내내 연결을 유지한다. 로비/인게임 상태와 무관하게 항상 활성 상태다.
- **전환**: 매치 확정 시 로비 gRPC 채널을 닫고 NGO로 게임 서버에 접속한다. 이 시점에 `playerId + matchToken` 페이로드를 포함해 ConnectionApproval 검증을 받는다.
- **재접속 정책**: 게임 종류에 따라 다르다. 재접속 허용 여부는 방 단위로 결정한다. 싱글플레이는 방 1개당 유저 1명이라 사실상 플레이어 단위와 동일하다. (상세 구현 예정)
- **ClientInstance**: `DontDestroyOnLoad` 싱글톤. 씬 전환 시에도 유지되며 `LobbyClient`, `ChatClient`, 추후 `IngameClient`를 들고 있다. 언리얼의 `GameInstance`에 대응하는 개념.

### 마스터 서버

모든 메인 서버(로비, 게임 에이전트, 채팅)의 허브 역할을 한다.

- 각 서버 인스턴스의 부하/수용량을 추적하는 인스턴스 레지스트리를 관리한다.
- 로드 밸런싱으로 신규 클라이언트를 적절한 서버로 배정한다. (예정)
- 게임 에이전트에 매치 배정(`RequestIngameAssignment`)을 처리한다.
- 드레인 신호로 특정 서버의 신규 접속을 차단한다.
- 하트비트 타임아웃 감지 시 해당 서버의 잔업을 처리한다. (예정)
- 매칭/채팅 등 비즈니스 로직은 담당하지 않는다.

### 로비 서버

클라이언트가 로비 상태에서 접속하는 서버다.

- 클라이언트와 gRPC로 통신한다. Login, CreateAccount, Logout, RequestMatch, CancelMatch를 처리한다.
- JWT 기반 인증을 담당한다. `AuthInterceptor`가 모든 요청에서 토큰을 자동 검증한다.
- 매치 확정 시 Redis 세션을 `Moving`으로 업데이트하고 일회용 `MatchToken`을 발급한다.
- 로그인/로그아웃 시 Redis Session Registry를 갱신한다.
- 마스터 서버와 gRPC 연결을 유지하며 부하를 주기적으로 보고한다.

### 채팅 서버

로비와 인게임 경계를 넘나드는 채팅을 담당한다. 표층 서버로 클라이언트와 직접 연결된다.

- 클라이언트와 WebSocket + Protobuf으로 로그인~로그아웃 내내 연결을 유지한다.
- 마스터 서버와 gRPC로 등록/상태 보고를 한다.
- Redis에서 플레이어 세션을 조회해 채팅 허용 여부를 판단한다.
- Redis Pub/Sub으로 채팅 서버 인스턴스 간 메시지를 동기화한다.
- 채널 종류: 친구(`friend:{friendshipId}`), 귓말(`whisper:{userA}:{userB}`), 길드(`guild:{guildId}`)
- 인게임 내 채팅(팀/매치)은 NGO가 담당하며 채팅 서버와 무관하다.
- Kafka로 채팅 로그/욕설 이벤트를 발행한다. (예정)
- RabbitMQ로 신고 처리 work queue에 발행한다. (예정)
- `IChatMessageStore`로 메시지 저장을 추상화한다. 현재는 인메모리 구현체 사용.
- 필터링은 `IChatFilter`로 추상화돼 있으며 현재는 더미 구현체 사용.

### 게임 서버 (Unity Dedicated Server)

NGO 기반 인게임 서버다.

- `IngameServerBootstrap`이 게임 에이전트와 gRPC 연결을 맺은 후 NGO 서버를 시작한다.
- `ConnectionApprovalCallback`에서 클라이언트의 `playerId + matchToken`을 게임 에이전트에 전달해 검증을 위임한다. 직접 Redis에 접근하지 않는다.
- 검증 결과(`ApprovalResponse`)를 받아 NGO 접속을 승인하거나 거부한다.
- 플레이어 접속/퇴장 시 게임 에이전트에 알린다.
- `clientId ↔ playerId` 매핑을 관리한다.

### 게임 에이전트

게임 서버(Unity)의 사이드카 역할을 한다. Unity에서 직접 처리하기 어려운 서버 로직을 담당한다.

- 게임 서버와 gRPC 양방향 스트림으로 연결된다.
- `ConnectionApproval` 검증을 담당한다. Redis에서 세션을 조회해 `Moving` 상태와 `MatchToken` 일치 여부를 확인한다. 검증 성공 시 토큰을 소비하고 세션을 `Ingame`으로 업데이트한다.
- 플레이어 접속/퇴장 시 Redis Session Registry를 업데이트한다.
- 마스터 서버에 게임 서버의 부하/수용량을 보고한다.
- 매치 결과를 Kafka에 발행한다. (예정)
- `clientId ↔ playerId` 매핑을 관리한다.

### 매칭 서버 [예정]

매치메이킹 전담 서버다.

- Redis Sorted Set으로 실력 점수 기반 매칭 대기열을 관리한다.
- 매칭 전 각 로비 서버에 플레이어 생존 여부를 확인한다.
- 매치 확정 시 마스터 서버에 게임 서버 배정을 요청한다.
- 매칭 수락 화면을 지원한다. 수락/거절/타임아웃 모두 동일하게 처리한다(해당 플레이어 제외 후 재매칭).

### 백업 서버 [예정]

마스터 서버를 제외한 다른 서버가 죽었을 때 대신 업무를 이어받는다. 상세 설계는 추후 논의.

### Redis Session Registry

플레이어 상태의 단일 진실 공급원이다.

- 모든 서버가 동일한 Redis를 바라봐서 어느 서버든 플레이어의 현재 상태를 조회할 수 있다.
- 플레이어 상태: `Lobby → Matching → Moving → Ingame → Moving → Lobby`
- `Moving` 상태는 서버 이동 중임을 나타낸다. 이 시점에 `NextServer`와 일회용 `MatchToken`이 저장된다. 다른 서버에서 해당 플레이어인 척 접속하려 해도 `MatchToken` 불일치로 차단된다.
- 롤링 토큰 패턴으로 재접속 시 토큰을 재사용한다. Redis List로 최신 토큰 2개를 유지해 클라가 새 토큰을 받기 전에 크래시해도 이전 토큰으로 재접속 가능하다. (예정)
- TTL로 클라이언트 크래시 시 세션을 자동 만료한다.
- 방 상태 관리(`room:{roomId}:info`, `room:{roomId}:players`)도 담당한다. (예정)

### 브로커

**Kafka**: 매치 결과, 게임 이벤트, 채팅 로그 등 분석/집계가 필요한 대용량 이벤트를 처리한다. 수십ms 지연이 있어 매칭같은 실시간 처리에는 부적합하다.

**RabbitMQ**: 보상 지급, 결제 처리, 신고 처리 등 정확히 한 번 처리가 보장돼야 하는 작업에 쓴다. Work queue 패턴으로 하위 서버들이 소비한다. Fanout exchange로 운영 공지를 브로드캐스트한다.

**Redis**: Session Registry, 매칭 대기열, 채팅 Pub/Sub, 방 상태 관리에 쓴다. 인메모리라 응답이 빠르고 TTL로 자동 만료가 가능하다.

---

## 보안

### ConnectionApproval
클라이언트가 게임 서버(NGO)에 접속할 때 `playerId + matchToken` 페이로드를 포함한다. 게임 에이전트가 Redis에서 세션을 조회해 `Moving` 상태와 토큰 일치 여부를 검증한다. 검증 실패 시 접속을 거부한다.

### Moving 상태
서버 이동 중인 플레이어는 Redis에 `Moving` 상태로 기록된다. 해커가 타인의 토큰을 들고 다른 서버에 접속하려 해도 Redis의 `NextServer`와 `MatchToken`으로 차단된다.

### JWT
로비 서버와의 통신은 JWT로 인증한다. `AuthInterceptor`가 모든 gRPC 요청에서 토큰을 자동 검증한다. Login/CreateAccount는 토큰 없이 통과한다. 현재는 더미 토큰(`DummyTokenProvider`) 사용 중이며 추후 실제 JWT로 교체 예정.
