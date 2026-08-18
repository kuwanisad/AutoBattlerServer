# TODO

미완성 작업, 논의 중인 사항, 추후 구현할 것들을 모아둔다.
구현 완료 시 항목을 삭제하지 말고 [완료] 표시와 함께 남겨둔다.

---

## 논의 중

### 재접속 정책

**결정된 것**
- 재접속 허용 여부는 방 단위로 결정한다.
- 싱글플레이는 방 1개당 유저 1명이라 사실상 플레이어 단위와 동일하다.
- 방이 타임아웃을 감지한 후에 세션 레지스트리를 수정한다.
- Redis를 단순 Session Registry가 아닌 게임 객체 상태 관리 저장소로 확장한다.
  - `session:{playerId}` ← 플레이어 세션 + OwnerServerId (기존 + 갱신)
  - `room:{roomId}:info` ← 방 상태 (추가 필요)
  - `room:{roomId}:players` ← 방에 있는 플레이어 목록 (추가 필요)
  - `server:{serverId}` ← 서버 생존 TTL (메인 서버가 5초마다 갱신, TTL=10초)
- 재접속 시 로비 서버가 방 상태를 조회해서 게임이 아직 진행 중인지 확인 후 지시한다.
- 서버 생존 TTL과 객체 권한 관리를 Redis(Session Registry)가 담당한다.
  - 서버 하트비트 간격: 5초
  - 서버 TTL: 10초
  - 권한 이전 판단은 Lua 스크립트로 원자적 처리 (TryClaimOwnershipAsync)
  - 클라가 새 서버에 접속 시 TryClaimOwnership으로 권한 이전 요청
- MatchToken 제거: 매칭 서버가 게임 에이전트에 플레이어 명단을 전달하므로 불필요
  - ConnectionApproval은 OwnerServerId 검증으로 대체

**미결 사항**
- `PlayerSession`에 추가할 필드 확정
  - `RoomId` ← 어느 방에 있는지 (재접속 시 필요)
  - `AllowReconnect` ← 방의 재접속 허용 여부
  - `ReconnectDeadlineUnixMs` ← 재접속 가능 마감 시각
- `LoginResponse`에 재접속 정보(`RedirectInfo`) 추가 여부
- Moving 상태에서 로그인 시 처리 흐름 확정
  - 재접속 가능한 방이고 게임 진행 중: 인게임 서버 주소 반환
  - 재접속 불가능한 방 또는 게임 종료: 세션 Lobby로 초기화 + 패널티 적용 여부
- `ingame.proto`의 `OpenRoom`에 `allow_reconnect` 필드 추가

### Redis 장애 대응

**결정된 것**
- Redis Sentinel 또는 Cluster로 고가용성 확보 (운영 환경)
- 서킷 브레이커 패턴으로 Redis 장애 시 제한적 동작 모드 전환
- 운영 환경에서 Redis RDB + AOF 하이브리드 영속성 활성화

**미결 사항**
- 서킷 브레이커 구현 방식 및 각 서버별 폴백 동작 확정
- 개발 환경에서는 단일 Redis로 유지, 운영 환경 전환 시 Sentinel/Cluster 적용

### RegistryCleanupServer 폐기 이유

초기에는 서버가 죽었을 때 플레이어 세션을 정리하는 전담 서버로 설계했다.
명부(`Dictionary<serverId, HashSet<objectId>>`), GraveList(`HashSet<string>`) 등을
메모리에 유지하는 구조였는데 다음 문제들이 있었다.

- RegistryCleanupServer 자체가 죽으면 명부가 소멸
- 명부 영속화를 위해 Redis를 쓰면 결국 Redis가 모든 정보를 들고 있게 됨
- Redis가 서버 생존 TTL과 객체 권한 보유자를 직접 관리할 수 있음
- 권한 이전 로직을 Lua 스크립트로 원자적 처리 가능
- 클라 재접속 시 TryClaimOwnership으로 그 시점에 처리하면 별도 잔업 처리 불필요
- 결론: RegistryCleanupServer 없이 Redis + 메인 서버로 처리 가능

### 마스터 서버 역할 재정립 후 정리 필요

매칭 서버 설계 완료 후 아래 항목들을 정리해야 한다.

**마스터 서버 역할 (재정립)**
- [완료] 접속한 서버 인증 (우리 서버인지 검증)
  - `IServerAuthenticator` 퍼사드 인터페이스 도입. 지금은 `DummyServerAuthenticator`(항상 통과).
    나중에 mTLS 도입 시 `CompositeServerAuthenticator`(여러 검증기 조합)로 교체 예정.
    위치: `MasterServer/Security/`
- [완료] 메인 서버들에 매칭 서버 주소 전달
  - `ConnectionHandler`가 메인 서버 최초 접속 시 이미 등록된 매칭 서버 주소를 즉시 전달.
    매칭 서버가 재등록될 때(재시작 등)는 접속 중인 메인 서버 전체에 브로드캐스트.
- [완료] 메인 서버들에 세션 레지스트리(Redis) 주소 전달
  - `master.proto`에 `SessionRegistryInfo` 추가. 마스터는 `shared.appsettings.json`의
    `Redis:ConnectionString`을 자체적으로 읽어서(신규: `Program.cs`에서 로드), 접속하는
    모든 서버 타입(매칭 서버 포함)에 최초 등록 시 바로 전달한다.
  - 로비/게임 에이전트/채팅/매칭 서버는 더 이상 Redis 주소를 로컬 설정에서 읽지 않는다.
  - `SessionRegistryConnector`(`Shared.Common/Redis/`)가 `ISessionRegistry`와
    `ISessionRegistryConnector`를 동시에 구현하는 프록시로 동작한다. 연결 전에 호출되면
    첫 연결까지 대기했다가 위임한다 - 순서 문제 없이 기존 코드(`LobbyControlService`,
    `MatchEngine`, `GameServerHandler`, `ChatChannelManager`)는 그대로 둘 수 있었다.
  - `MasterConnectionService`가 하트비트 루프 안에서 직접 `RefreshServerHeartbeatAsync`를
    부르던 걸 제거하고, 별도 `ServerHeartbeatService`(`MainServer.Common/Heartbeat/`)로 분리.
    이유: `MasterConnectionService`가 `ISessionRegistry`(→ 마스터 접속 후에야 연결됨)에
    직접 의존하면 순환이 생기기 때문. `ServerHeartbeatService`는 프록시가 대기를
    흡수해주므로 시작 순서를 신경 안 써도 된다.

**제거 완료**
- [완료] `MasterServer/Services/CleanupServerConnection.cs` 제거
- [완료] `RegistryCleanupServer` 프로젝트, `Shared/RegistryCleanupServer.Contracts` 프로젝트 전체 삭제 (슬루션에서도 제거)
- [완료] `ConnectionHandler.cs`에서 RegistryCleanupServer 통보 코드 제거
  - 단, `ShutdownFlag`(정상/비정상 종료 구분)는 로그용으로 남겨둠. `master.proto`의
    `ShutdownNotice` 자체를 없앨지는 아직 미정 (아래 항목 참고).
- [완료] `MasterServer.csproj`에서 `RegistryCleanupServer.Contracts` 참조 제거

**미결 사항**
- `master.proto`의 `ShutdownNotice` 제거 여부 - 마스터가 잔업 처리를 안 하므로 불필요해질 수 있으나,
  로그상 정상/비정상 종료 구분용으로는 남겨둘지 판단 필요.
- **`MasterConnectionService`가 `ShutdownNotice`를 실제로 보내는 코드가 없음** - `ConnectionHandler`는
  수신 시 정상 종료로 처리하는 로직이 있지만, `MasterConnectionService`엔 `StopAsync` 오버라이드가
  없어서 이 메시지가 한 번도 발신되지 않는다. 즉 지금은 항상 "비정상 종료"로만 로그가 남는다.
  - graceful shutdown을 구현하려면: (1) `MasterConnectionService.StopAsync`에서 `Shutdown` 메시지
    전송, (2) `stop-servers.ps1`이 `taskkill /F` 대신 각 프로세스에 정상 종료 신호를 보내도록 수정
    (새 콘솔 창으로 띄운 프로세스라 Ctrl+C 전달이 간단치 않음 - 방법 검토 필요) 이 둘 다 필요함.
  - 지금까지 테스트는 계속 `start/stop-servers.ps1`로 해왔으므로 당장 급한 건 아님. 나중에 처리.

---

## 구현 필요

- 퇴장 후 로비 복귀 검증 테스트
- Redis 방 상태 관리 (`room:{roomId}:info`, `room:{roomId}:players`)
- **매칭 서버 구현** (범용 라우팅/매칭 엔진)
  - 플레이어 풀, 인게임 서버 풀, 로비 서버 풀 관리
  - JoinMatch / LeaveMatch (로비 서버 → 매칭 서버)
  - RegisterServer / UnregisterServer (메인 서버 → 매칭 서버)
  - PrepareMatch (매칭 서버 → 게임 에이전트)
  - MatchReady (게임 에이전트 → 매칭 서버)
  - MatchConfirmed / MatchCancelled (매칭 서버 → 로비 서버)
  - 매칭 타임아웃 시 플레이어 Lobby로 복귀
  - 실력 점수 기반 매칭 알고리즘 (TODO: 더미로 우선 구현)
- `ARCHITECTURE.md` 갱신 (매칭 서버 구현 완료 시)

---

## 구현 완료

- [완료] Redis Session Registry 기본 구조
- [완료] 로비 서버 ↔ Redis 세션 등록/업데이트
- [완료] 게임 에이전트 ↔ Redis 세션 업데이트
- [완료] NGO ConnectionApproval (playerId + matchToken 검증)
- [완료] clientId ↔ playerId 매핑
- [완료] 채팅 서버 구현 (WebSocket + Protobuf, 채널 입장/퇴장/메시지 송수신)
- [완료] 프로젝트 구조 정리 (GameProject1/DevTools, Unity, Server)
- [완료] Directory.Build.props 통합 (절대 경로 제거)
- [완료] RegistryCleanupServer 폐기 (설계 논의 후 Redis로 대체)
- [완료] ISessionRegistry 확장 (서버 생존 TTL, TryClaimOwnershipAsync, OwnerServerId)
- [완료] PlayerSession에 OwnerServerId 추가 (세션과 권한 통합)
- [완료] MatchToken 제거 결정 (매칭 서버 구현 시 ConnectionApproval 수정 예정)
