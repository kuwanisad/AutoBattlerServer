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
- **[변경] 세션 레지스트리는 "관리 권한 기록부"로만 쓴다. 방 상태는 Redis에 안 둔다.**
  - `room:{roomId}:info`, `room:{roomId}:players` 안 만들기로 결정 (아래 이유).
  - 방은 그 방을 열고 있는 인게임 서버와 운명을 같이 한다 - 그 서버가 죽으면 방도 같이 사라지고,
    다른 서버가 이어받는 시나리오 자체가 없다. 그러니 방 상태를 외부(Redis)에 복제해봤자
    "죽은 서버 대신 이어받기"에 못 쓴다 - 애초에 목적이 성립하지 않는다.
  - 유저가 어느 방 소속인지 같은, 그 서버가 스스로 알고 있어야 할 업무 데이터는 각 서버
    내부 메모리(`unordered_map<유저ID, 방ID>` 등)에 두면 된다. 세부 구조는 추후 논의.
- 재접속 시 흐름: 클라가 로그인 후 `EnterLobby` RPC를 호출하면, 로비가 세션 레지스트리에
  자신으로 소유권 이전을 시도한다. 기존 소유자(주로 인게임 서버)가 아직 살아있으면 거부되고,
  그 서버 주소가 같이 반환된다 - 로비는 이걸로 "진행 중인 게임이 있습니다" 안내를 클라에게 띄울 수 있다.
  (기존 소유자가 죽어있으면 정상적으로 로비가 소유권을 가져오고 EnterLobby는 성공한다.)
- 서버 생존 TTL과 객체 권한 관리를 Redis(Session Registry)가 담당한다.
  - 서버 하트비트 간격: 5초 / 서버 TTL: 10초
  - **[변경] 서버 등록과 하트비트를 분리했다.** 기동 시 `RegisterServerAsync(id, address)`로
    ID+주소를 한 번 등록하고, 이후 하트비트는 `RefreshServerHeartbeatAsync`가 TTL만 갱신한다
    (값 재전송 없음 - 하트비트에 불필요한 부하를 주지 않기 위함).
  - 권한 이전 판단은 Lua 스크립트로 원자적 처리 (`TryClaimOwnershipAsync` /
    `TryClaimOwnershipWithPreviousOwnerAsync` - 후자는 거부 시 기존 소유자 정보까지 반환).
  - `GetOwnerAsync`(순수 조회), `GetServerAddressAsync` 추가.
  - 클라가 새 서버에 접속 시 TryClaimOwnership으로 권한 이전 요청
- **[완료] MatchToken 제거**: `PlayerSession.MatchToken`, `ingame.proto`의 `ApprovalRequest.match_token`
  모두 제거. 매칭 서버가 게임 에이전트에 플레이어 명단을 전달하므로 불필요.
  ConnectionApproval은 OwnerServerId 검증으로 대체.
  - **주의**: Unity 쪽에 아직 `MatchToken`을 쓰는 코드가 남아있다 (`GrpcIngameServerChannel.cs`,
    `ClientInstance.cs`의 NGO 접속 payload, `ILobbyChannel.cs`/`GrpcLobbyChannel.cs`). 서버
    proto가 바뀌었으니 Unity 쪽도 맞춰서 고쳐야 컴파일된다. 별도 작업으로 처리 필요.

**미결 사항**
- Moving 상태에서 로그인 시 처리 흐름 확정
  - 재접속 가능한 방이고 게임 진행 중: 인게임 서버 주소 반환
  - 재접속 불가능한 방 또는 게임 종료: 세션 Lobby로 초기화 + 패널티 적용 여부
- `ingame.proto`의 `OpenRoom`에 `allow_reconnect` 필드 추가 [동의함, 아직 구현 안 함]
- 방 소속 정보(유저ID→방ID)를 각 서버가 어떤 자료구조로 관리할지 세부 설계

**⚠ 새로 발견한 별개 버그 - `NotifyMatch` 콜백이 항상 실패함**
`MatchCallbackClient`(매칭 서버)가 `LobbyMatchControl.NotifyMatch`를 호출하는데, 이 서비스는
`MatchingServer`의 `LobbyMatchService`에만 구현돼 있고(`NotifyMatch`는 `Unimplemented`를 던짐 -
주석에 "실제 구현은 로비 서버에 있다"고 적혀 있지만 그 클래스 자체가 없음), **로비 서버는
`LobbyMatchControl` 서비스를 아예 구현/등록하지 않았다.** 즉 매칭 확정/취소 통보가 지금 구조로는
항상 실패한다 (`MatchCallbackClient`가 예외를 잡아서 로그만 남기고 조용히 삼킴 - 크래시는 안 나지만
플레이어는 결과를 영원히 못 받음). `lobby.proto`의 `LobbyControl.NotifyMatch`(로비 서버가 실제
구현한 것)와 `matching.proto`의 `LobbyMatchControl.NotifyMatch`가 이름은 같지만 다른 gRPC
서비스 경로라 서로 안 이어진다. 다음에 다룰 것.

### 매칭 흐름 설계 (정상 흐름 + 장애 처리)

`MatchEngine`이 지금은 더미(1명이면 즉시 매칭)라서 아직 구현 전이지만, 정상 흐름과 장애
처리(접속 끊김, 인게임/로비 서버 다운)를 먼저 설계했다. 실제 구현은 `## 구현 필요`의
"매칭 서버 구현" 항목에서 진행한다.

**정상 흐름**
1. 로비 서버가 매칭 원하는 클라를 매칭 서버에 등록 (`JoinMatch`)
2. 매칭 서버가 클라들을 선별해 가상의 방(matchId) 생성, 선별된 클라 투입
3. 매칭 서버가 적절한 게임 에이전트에게 클라 목록 + 방 ID 제공, 방 생성 요청 (`PrepareMatch`)
4. 게임 에이전트가 요청을 받아 인게임 서버에 전달 (`OpenRoom`)
5. 인게임 서버가 방을 열고 게임 에이전트에 알림 (`RoomOpened`)
6. 게임 에이전트가 매칭 서버에 알림 (`PrepareMatchResult`)
7. 매칭 서버가 방 안 클라들이 속한 로비 서버에게 인게임 서버 주소 전달 (`NotifyMatch`)
   - 7.1 가상의 방 제거
8. 로비가 세션 레지스트리에 클라 관리 권한을 인게임 서버로 변경
   (실제로는 로비가 `UpdateAsync`로 먼저 넘겨두고, 이후 클라가 실제 NGO 접속할 때
   게임 에이전트가 `TryClaimOwnershipAsync`로 한 번 더 원자적으로 확인하는 2단계 구조)

**현재 구현 상태 (설계 대비)**
- 1, 3, 6, 7, 7.1, 8: 구현됨 (7은 예전엔 `LobbyMatchControl.NotifyMatch` 이중 정의 버그로
  도달 불가였는데, `MatchConnectionControl` 스트림으로 바뀌면서 해결됨)
- 2: 더미 (선별 로직 없음, 1명이면 즉시 매칭) - 후순위로 미루기로 함
- **4, 5: 구현 완료** - `GameServerHandler.RequestOpenRoomAsync` + `PendingWorkTracker`로
  실제 Unity 게임 서버에 `OpenRoom` 전송 후 `RoomOpened` 응답을 기다림. Unity 쪽도
  `GameManager`/`RoomManager` 구조로 실제 방 생성 처리함.

**[완료] 접속 끊김/서버 다운 처리 - Queued/PreparingRoom 단계**

`Shared.Common/Transactions/CompensatingTransaction.cs` 신설 - 여러 단계를 거치는 작업이
남긴 부작용을 스택으로 쌓아뒀다가, 중단되면 역순(LIFO)으로 되돌리는 범용 유틸 (매칭
도메인과 무관하게 재사용 가능하도록 상속이 아니라 조합으로 설계).

`MatchEngine`에 반영:
- `PendingMatch`에 `Phase`(`PreparingRoom`/`Crossing`)와 `Transaction` 필드 추가
- `_playerToMatch`(playerId → matchId) 역인덱스 신설 - 끊긴 플레이어가 어느 매치
  소속인지 즉시 조회
- `_onMatchDisrupted: Dictionary<MatchPhase, Action<PendingMatch>>` - 단계별 정책만
  여기서 결정하고, 실제 되돌리기는 `Transaction.Rollback()`에 위임 (감지 계층과 판단
  계층 분리)
- 진입점 2개: `LeaveMatch(playerId)`(기존 메서드를 확장 - Queued든 PreparingRoom이든
  다 처리), `HandleServerDisconnected(serverId, serverType)`(신규)
- `MatchConnectionHandler`가 스트림 끊김을 감지하면 `HandleServerDisconnected` 호출하도록
  배선 완료 - 게임 에이전트 다운이든 로비 다운이든 이제 매치 정리까지 자동으로 이어짐

**[미구현] Crossing 단계**
게임 에이전트가 "플레이어가 실제로 NGO 접속을 완료했다"를 매칭 서버에 보고하는 경로가
없다 (`GameServerHandler.HandlePlayerJoinedAsync`는 지금 세션 레지스트리만 갱신하고
끝 - 매칭 서버로는 아무것도 안 감). 그래서 `PrepareMatch` 성공하면 지금도 예전처럼
그 자리에서 바로 확정 처리하고 끝냄 - "넘어가는 중" 상태 자체를 추적하지 않음.
`MatchPhase.Crossing`은 열거형에 존재하지만 `_onMatchDisrupted`에 실제 정책 없이
에러 로그만 찍는 자리표시자로 남아있음. 이걸 채우려면:
1. 게임 에이전트 → 매칭 서버로 "플레이어 접속 완료" 보고하는 RPC/메시지 신설
2. `HandlePrepareMatchResultAsync`가 성공 시 바로 확정 통보하지 않고 `Phase = Crossing`으로
   전이한 뒤, 전원 접속 완료 시(또는 타임아웃 시) 확정/취소를 가름
3. "게임 에이전트한테 방 제거(취소) 요청"하는 RPC/메시지도 아직 없음 (지금 `matching.proto`/
   `ingame.proto`엔 방을 취소시키는 명령 자체가 없음) - Crossing 단계 중단 처리에 필요

**[미구현] 클라 → 새 로비 리다이렉트 메시지**
"일부 클라 미접속으로 방 취소" 시나리오(Crossing 단계 관련이라 위와 묶여있음) - 이미
NGO 접속한 클라에게 "방 닫힘 + 새 로비 주소"를 알려주는 `ingame.proto` 메시지 신규 필요.

**클라 접속 끊긴 경우**
```
매칭 중
  클라 리스트에서 제거

매칭 완료
  인게임 서버에 방 없음
    인게임 서버에 방 요청한 경우 → 게임 에이전트에 방 제거 요청
    클라 리스트에서 제거
    가상의 방 제거, 남은 클라 전부 클라 리스트로 복귀

  인게임 서버에 방 있고 클라들이 넘어가기 전
    게임 에이전트에 방 제거 요청 (무조건 - 이미 방이 열려있으므로)
    클라 리스트에서 제거
    가상의 방 제거, 남은 클라 전부 클라 리스트로 복귀

  인게임 서버에 방 있고 클라들이 넘어가는 중
    일정 시간 안에 클라 모두 접속 → 게임 시작
    일정 시간 안에 일부 클라 접속 못함 → 방 제거 (아래 이유로 전원 취소)
      매칭 서버가 적절한 로비 서버 요청 성공
        접속한 클라 → 세션 권한을 그 로비로 변경 + 클라 이동 안내
        접속 중인 클라(입장할 방 유무로 구분) → 동일 처리
      매칭 서버가 적절한 로비 서버 요청 실패 → 고정 주기로 재시도

게임 중
  일정 시간 안에 재접속 → 재개
  일정 시간 안에 재접속 실패 → 세션 레지스트리에서 세션 완전 삭제
    (OwnerServerId만 비우는 게 아니라 RemoveAsync로 통째로 지움 - 서버들 입장에서
    "이제 없는 사람"으로 확실히 하기 위함. 재로그인부터 다시 해야 함.)
```

**"일부만 접속 실패 시 방 전체 취소" 정책 - 의도적 결정**
리그 오브 레전드처럼 결원 있는 채로 시작하는 방식도 있지만, 여기서는 공정성을 우선해서
전원 취소로 결정. 넘어가는 "중"에 누군가 못 넘어왔다는 건 불공정한 경기가 시작될
위험이라고 보기 때문. (다른 게임이 다르게 하는 것과는 별개로, 이 프로젝트의 선택.)

**클라 → 새 로비로 리다이렉트 방법 (방 취소 후)**
클라가 매치 확정 시(`MatchFound`) 원래 로비 스트림을 이미 닫아버리므로(NGO 접속 직전),
방 취소 시점엔 로비를 통해 직접 push할 채널이 없다. 그래서 클라의 상태별로 다르게 처리:
- **이미 NGO로 접속한 클라**: 살아있는 그 연결을 통로로 써서 바로 알려준다.
  `ingame.proto`에 "방 닫힘 + 리다이렉트 주소" 메시지 신규 필요 (게임 에이전트가 릴레이).
- **접속 시도 중인 클라**: `ConnectionApprovalCallback` 시점에 방이 이미 없으면 그 자체가
  거부 사유가 된다 (신규 로직 불필요 - 자연스럽게 처리됨).
- 위 두 경우 다, 클라는 결국 "접속 실패 인지 → 로비 재접속" 루트를 타게 되는데, 매칭
  서버가 미리 세션의 `OwnerServerId`를 새 로비로 박아두기만 하면 `EnterLobby`의 소유권
  충돌 검사가 자동으로 올바른 로비로 안내해준다. 매칭 서버가 클라에게 "어느 로비로
  가라"고 직접 지정 배정/재시도하는 로직을 별도로 안 만들어도 됨 - 클라의 재접속
  재시도 루프가 그 역할을 흡수한다.

**인게임 서버가 터진 경우**
```
매칭 중 (PreparingRoom)
  [완료] RPC 스트림이 끊겼을 때 서버 목록에서 원자적으로 제거 + MatchEngine.HandleServerDisconnected로
  관련 매치까지 정리(Rollback + 대기열 복귀)

매칭 완료 / 게임 중 (동일)
  세션 레지스트리 서버 권한 TTL 만료 안 됨 → 클라 입장에서는 재접 시도 → 접속 불가 (그 이상 불가능)
  세션 레지스트리 서버 권한 TTL 만료 → 클라가 접속한 로비가 권한 인계
    (이미 구현됨 - EnterLobby의 TryClaimOwnershipWithPreviousOwnerAsync가 정확히 이 역할)
```

**로비 서버가 터진 경우**
```
매칭 중 (Queued/PreparingRoom)
  [완료] RPC 스트림이 끊겼을 때 서버 목록에서 원자적으로 제거
  [완료] 해당 로비 소속 클라 전부 방/클라 리스트에서 제거 (MatchEngine.HandleServerDisconnected)
    방 일부에 해당 로비 소속이 있는 경우 → 방 제거, 남은 클라는 클라 리스트로 복귀

매칭 완료 (Crossing)
  → Crossing 단계 자체가 아직 미구현이라 이 경로는 아직 안 탐 (위 "Crossing 단계" 참고)

게임 중
  영향 없음 (이 시점엔 세션 권한이 이미 인게임 서버로 넘어가 있어서 로비가 죽어도 무관)
```

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
- **채팅 서버 - `IChatMessageStore`를 공유 저장소로 교체**
  - 지금 `InMemoryChatMessageStore`는 인스턴스 로컬 `ConcurrentDictionary`라서, 플레이어가
    다른 채팅 서버 인스턴스로 재접속하면 새 인스턴스는 그 채널의 과거 기록을 모른다.
    (세션 레지스트리가 로컬 config였을 때 겪었던 것과 같은 종류의 문제.)
  - "서버 이동 시 이전 채팅 기록을 다시 불러와야 한다"는 요구사항은 이걸로 해결해야 하고,
    Kafka/RabbitMQ로 해결할 문제가 아니다 - Kafka는 메시지를 그 공유 저장소로 흘려보내는
    파이프라인 역할 정도는 할 수 있지만, 조회 가능한 저장소 자체를 대체하진 못한다.
  - Redis(리스트/정렬 집합) 또는 DB로 교체하는 방향. `ISessionRegistry`를 지연 연결 프록시로
    바꿨던 방식(`SessionRegistryConnector`)을 참고할 수 있음.
  - `ChatServer/Program.cs`에 등록만 돼 있고 실제로는 안 쓰이는 `AddMainServerMessaging()`
    (Kafka/RabbitMQ 프로듀서)도 이 작업이나 모더레이션 파이프라인 설계 시 같이 정리할 것.
    (비속어 필터링 자체는 `IChatFilter`가 담당 - inline 방식이라 이것과는 별개.)

- **매칭 서버 - 정상 흐름 배관은 완료, 알맹이/장애 처리는 남음**
  - [완료] `MatchConnectionControl` 스트림으로 로비/게임 에이전트 접속 유지 - 예전
    `RegisterServer`/`UnregisterServer` 단항 + Redis TTL 폴링을 대체. 연결 끊기면
    (하트비트 타임아웃 포함) 즉시 서버 풀에서 제거됨.
  - [완료] `JoinMatch`/`LeaveMatch`(로비→매칭), `PrepareMatch`/`PrepareMatchResult`
    (매칭↔게임에이전트) - 전부 정상 동작.
  - [완료] 매칭 확정/취소 통보(`MatchNotification`) - 예전엔 `LobbyMatchControl.NotifyMatch`
    이중 정의 버그로 로비에 아예 안 닿았는데, 이제 위 스트림에 실어 보내는 방식으로 바뀌면서
    자연스럽게 해결됨. 로비 쪽은 `MatchNotificationDispatcher`가 수신 처리.
  - [완료] **게임 에이전트가 실제로 게임 서버(Unity)에 방을 생성하고 응답을 기다림.**
    `GameServerHandler.RequestOpenRoomAsync` + `PendingWorkTracker`로 비동기 왕복 처리.
    예전엔 `Task.Delay(100)`으로 무조건 성공을 흉내내던 더미였음.
  - [완료] Unity 쪽 `GameManager`(정적 클래스, 매니저 총괄) / `RoomManager`(방 여러 개
    동시 관리, 유저↔방ID 캡슐화) / `IngameServerInstance`(옛 IngameServerBootstrap, 채널
    허브 역할만) 구조 신설. 연결 끊기면 `GameManager.Shutdown()`이 `ManagerType` 순서대로
    전부 정리 후 프로세스 종료 (되돌릴 수 없는 절차로 확정).
  - [완료] **클라 접속 끊김 / 로비·게임에이전트 다운 - Queued/PreparingRoom 단계**
    `Shared.Common/Transactions/CompensatingTransaction.cs`(범용 되돌리기 스택) +
    `MatchEngine`의 `_onMatchDisrupted` 단계별 델리게이트 + `HandleServerDisconnected`/
    `LeaveMatch` 진입점으로 구현 완료. 매칭 대기 중 클라 이탈, PrepareMatch 진행 중
    게임 에이전트/로비 다운 전부 자동으로 정리되고 남은 플레이어는 대기열로 복귀.
  - **[미구현] Crossing 단계 (방 열린 후 ~ 전원 접속 완료 전)** - 게임 에이전트가
    "플레이어 접속 완료"를 매칭 서버에 보고하는 경로가 없어서, 지금은 `PrepareMatch`
    성공하면 그 자리에서 바로 확정 처리하고 끝남 ("넘어가는 중" 상태 자체를 추적 안 함).
    `MatchPhase.Crossing`은 자리표시자만 있음. 필요한 것 3가지:
    1. 게임 에이전트 → 매칭 서버 "플레이어 접속 완료" 보고 RPC/메시지
    2. `HandlePrepareMatchResultAsync` 성공 시 바로 확정 대신 `Crossing`으로 전이,
       전원 접속 완료/타임아웃 시점에 가름
    3. 매칭 서버 → 게임 에이전트 "방 취소" RPC/메시지 (지금 없음)
  - **[미구현] 실력 점수 기반 매칭 알고리즘** - 지금은 더미(1명이면 즉시 매칭). 후순위로
    미루기로 함 (아직 게임 자체가 없어서 뭘 기준으로 매칭할지 정할 수 없음).
  - **[미구현] 클라 → 새 로비 리다이렉트 메시지** - Crossing 단계 중단 시 필요
    (위 항목과 묶여있음). 이미 NGO 접속한 클라에게 "방 닫힘 + 새 로비 주소"를 알려주는
    `ingame.proto` 메시지 신규 필요.
- `ARCHITECTURE.md` 갱신 필요 (매칭 서버 관련 설명이 오늘 나온 구조랑 안 맞음)

---

## 구현 완료

- [완료] 죽은 코드 정리: `Shared/MainServer.Common/Ingame/` 폴더 삭제
  (`IngameMainServer.cs`, `IIngameEventHandler.cs`, `MatchResult.cs`) - `GameServerHandler`가
  실제로 구현되기 전에 미리 스케치해둔 것으로 보이며, 아무 데서도 참조되지 않았음.
  단, `OnMatchFinishedAsync`가 하려던 일(매치 결과를 Kafka에 발행 + RabbitMQ로 보상 요청 발행)은
  `GameServerHandler.HandleMatchResultAsync`가 아직 로그만 찍고 비어있는 자리라, 나중에 실제
  구현할 때 이 삭제된 코드의 로직을 참고할 수 있음.
- `AdaptiveWaiter.cs`(`Shared.Common/Threading/`)는 유지하기로 결정 - 지금은 아무 송신
  루프도 안 쓰고 있지만(다들 `Task.WhenAny(timerTask, queueTask)`를 직접 구현), 나중에
  송신 루프들을 정리할 기회가 생기면 이걸로 통합하는 걸 고려할 것.

- [완료] Redis Session Registry 기본 구조
- [완료] 로비 서버 ↔ Redis 세션 등록/업데이트
- [완료] 게임 에이전트 ↔ Redis 세션 업데이트
- [완료] NGO ConnectionApproval (playerId 검증, OwnerServerId 기반 - matchToken은 제거됨)
- [완료] clientId ↔ playerId 매핑
- [완료] 채팅 서버 구현 (WebSocket + Protobuf, 채널 입장/퇴장/메시지 송수신)
- [완료] 프로젝트 구조 정리 (GameProject1/DevTools, Unity, Server)
- [완료] Directory.Build.props 통합 (절대 경로 제거)
- [완료] RegistryCleanupServer 폐기 (설계 논의 후 Redis로 대체)
- [완료] ISessionRegistry 확장 (서버 생존 TTL, TryClaimOwnershipAsync, OwnerServerId)
- [완료] PlayerSession에 OwnerServerId 추가 (세션과 권한 통합)
- [완료] MatchToken 완전 제거 (서버 proto + Unity 양쪽) - OwnerServerId 검증으로 대체
- [완료] 매칭 서버 접속 유지 스트림(`MatchConnectionControl`), 세션 레지스트리 동적 배포,
  게임 에이전트의 실제 방 생성(`RequestOpenRoomAsync`/`PendingWorkTracker`), Unity
  `GameManager`/`RoomManager` 구조, 접속 끊김 시 되돌리기(`CompensatingTransaction` +
  `MatchEngine` 단계별 델리게이트) - 전부 이번 세션에서 구현
  (설계 논의: 상태 패턴/사가(보상 트랜잭션)/이벤트버스/델리게이트 딕셔너리 비교 후
  "사가 + 가벼운 델리게이트" 조합 채택. `CompensatingTransaction`은 매칭 도메인과
  무관한 범용 유틸로 분리 - `PendingWorkTracker`처럼 나중에 재사용 사례가 더 생기면
  공통점을 보고 합칠지 재검토)
