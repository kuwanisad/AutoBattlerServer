# NGO(Netcode for GameObjects) 노트

인게임 서버(Unity Dedicated Server)의 방/게임 구조를 설계하면서 실제로 테스트해서
확인한 NGO 동작과, 그걸 바탕으로 확정한 설계 결정을 적어둔다. 여기 적힌 건 문서만
보고 추정한 게 아니라 실제로 붙여보고 검증한 것들이다 - 나중에 같은 삽질을
반복하지 않기 위한 기록.

## 1. CheckObjectVisibility는 1회성 게이트다

`NetworkObject.CheckObjectVisibility` 델리게이트는 살아있는 조건문이 아니다.
**오브젝트가 Spawn되는 순간, 그리고 새 클라가 접속하는 순간**에 그 시점 기준으로
딱 한 번만 평가되고 끝난다. 델리게이트 내부에서 참조하는 상태(예: "현재
오너인가")가 나중에 바뀌어도 NGO가 알아서 다시 물어봐주지 않는다.

**정정(2026-10-24, NGO 2.11.0 소스와 하네스 reconnect 실행으로 확인)**: 예전에 여기 "재접속은 새 클라 접속이라
게이트가 다시 평가돼 자동으로 해결된다"고 적었는데, 그건 재접속 때마다 오브젝트를 새로 스폰하던 시절 얘기다.
지금처럼 PlayerController/Pawn/방 오브젝트를 살려 두면 틀린다:
- 새 접속의 평가 시점은 **승인 직후 동기화를 시작할 때**다(`NetworkConnectionManager.HandleConnectionApproval` →
  `NetworkSceneManager.SynchronizeNetworkObjects` → `NetworkSpawnManager.UpdateObservedNetworkObjects`). 서버의
  `OnClientConnected` 콜백은 클라가 동기화를 끝낸 **뒤**에 불린다(`SceneEventType.SynchronizeComplete`).
- 그래서 `OnClientConnected`에서 바꾸는 상태(소유권, 방 접속자 목록)는 그 평가에 반영되지 않는다 - 그 상태를 보는
  델리게이트는 재접속 클라에게 false를 냈고, 오브젝트가 영영 안 갔다(10-23 하네스 reconnect: 재진입 뒤 Pawn/View/UI 없음,
  phase.changed 없음).
- 지금 구조: 보낼 대상은 방의 레플리케이션 트리(`Network/Framework/ReplicationTree.cs`)가 정하고, 재접속은 접속 완료 뒤
  트리가 그 좌석 경로를 `NetworkShow`한다(아래 2번 규칙). 델리게이트는 트리를 보고(좌석이 있는 clientId일 때만), 스폰
  때 평가에 쓰인다.

**하지만** 이미 접속해 있는 클라를 대상으로 가시성을 나중에 동적으로 바꾸고
싶은 경우(예: 전투 페이즈에 상대 보드를 보여준다, 탈락한 플레이어에게 관전
권한을 준다) `CheckObjectVisibility` 내부 조건을 아무리 잘 짜놔도 재평가가 안
일어난다. 이런 경우엔 아래 2번을 써야 한다.

## 2. 이미 접속한 클라의 가시성을 바꾸려면 NetworkShow/NetworkHide를 쓴다

**실제로 확인한 것**: `NetworkObject.NetworkShow(clientId)` /
`NetworkObject.NetworkHide(clientId)`로 이미 접속해 있는 특정 클라 하나에
대해서만 가시성을 켜고 끌 수 있다. 다른 클라에는 영향 없음.

`Despawn()` 후 다시 `Spawn()`하는 방식으로도 결과적으로 가시성을 바꿀 수는
있지만(스폰되는 순간을 인위적으로 다시 만드는 것이므로 1번 규칙에 부합), 이건
목적에 안 맞는 우회로라 지양한다:
- 이미 정당하게 보고 있던 **다른** 클라들한테도 그 오브젝트가 사라졌다 다시
  나타난다 (`OnNetworkDespawn`/`OnNetworkSpawn`이 불필요하게 다시 호출됨).
- `NetworkVariable` 값이 전부 처음부터 다시 리플리케이트된다.
- `NetworkObjectId`가 재발급될 수 있어서, 옛 ID를 캐싱해둔 코드가 있으면 끊긴다.

**규칙**: 동적 가시성 변경은 반드시 `NetworkShow`/`NetworkHide`로. Despawn/Spawn
은 정말로 오브젝트를 통째로 다시 만들어야 하는 상황에만 쓴다.

## 3. ChangeOwnership은 그 오브젝트를 이미 받고 있는(관찰자) 클라에게만 된다

**정정(2026-10-24, NGO 2.11.0 소스)**: 예전에 여기 "ConnectionApproval 승인 직후에 ChangeOwnership을 불러도 정상
동작한다"고 적었는데, 2.11.0에서는 아니다. `NetworkSpawnManager.ChangeOwnership`은 새 주인이 그 오브젝트의
관찰자(`Observers`)가 아니면 **아무것도 안 바꾸고 돌아간다**(경고는 로그 수준 Developer일 때만 - 평소엔 조용함).
승인 시점의 새 clientId는 아직 아무것도 받지 않았으므로 그때의 소유권 반환은 효과가 없었다(서버 로그 "소유권 반환"은
호출 뒤 무조건 찍혀서 증거가 못 됐다).

**규칙**: 소유권은 `NetworkShow` 뒤에 바꾼다. `NetworkShow`는 즉시 관찰자로 넣고 스폰 메시지는 프레임 끝에 보내므로,
같은 프레임에 `ChangeOwnership`하면 그 스폰 메시지에 새 주인이 실려 클라가 받는 순간 `IsOwner`다(그 사이 소유권 변경
메시지는 보류 중인 Show라 따로 안 감). 서버로 회수(`ChangeOwnership(ServerClientId)`)가 되려면 서버도 관찰자여야
한다 - `SpawnWithObservers`(기본 true)일 때만 서버가 들어간다. 그래서 방 오브젝트는 기본값으로 스폰한다.

## 4. 확정된 소유권/생명주기 규칙 (PlayerController)

- **오너**: 그 플레이어(연결 중일 때). 접속 끊기면 서버(`NetworkManager.ServerClientId`)
  로 회수, 재접속하면 다시 그 플레이어에게 돌려준다.
- **핵심 게임 스탯(골드/체력/레벨 등)의 `NetworkVariable.WritePermission`은 항상
  `Server`.** 오너가 누구든(그 플레이어든 잠깐 서버든) 실제 값을 쓸 수 있는 건
  서버뿐이므로, 소유권이 왔다갔다 해도 데이터 권위 문제가 생기지 않는다. 클라는
  값을 직접 못 쓰고 ServerRpc로 "요청"만 보낸다 - 서버가 검증 후 값을 쓰면 NGO가
  알아서 클라에 반영해준다 (언리얼처럼 명시적 ClientRpc "명령"이 굳이 필요
  없음 - 값 변경 자체가 암묵적 통지 역할을 겸함). 순수 클라 로컬 상태(레디 체크
  등 틀려도 게임 결과에 영향 없는 것)에 한해서만 `WritePermission = Owner`를
  검토할 수 있다.
- **`NetworkObject.DontDestroyWithOwner`는 그 방 `IGameMode.AllowReconnect` 값을
  그대로 사용한다.** 프리팹에 고정하지 않고 스폰 시점에 런타임으로 설정 - 게임
  모드가 늘어나도 프리팹 하나로 대응 가능.
- **가시성**: PlayerController는 그 플레이어(좌석) 한 명한테만 보이도록 레플리케이션 트리의 플레이어 노드에 넣는다
  (오너가 아니라 좌석 기준 - 재접속으로 clientId가 바뀌어도 그대로 맞다).
  다른 플레이어가 이 플레이어의 공개 정보(체력/레벨 등)를 봐야 한다면 그건
  `GameState`가 별도로 노출한다 - PlayerController를 다른 클라에 보여주는 방식이
  아님.

## 5. 방 구조: Room(컨테이너) / GameMode(규칙) / GameState(공용 정보) / PlayerController(개인)

언리얼의 UWorld/GameMode/GameState/PlayerController 대응 관계를 따른다:

```
Room (World 격 컨테이너 - 물리적으로 독립된 PhysicsScene 하나에 대응)
 ├─ IGameMode (GameType이 결정 - 게임 규칙 권위자. NormalGameMode 등)
 ├─ GameState (NetworkObject, 방 전체 공용 정보 - 그 방 클라 전원에게 레플리케이트)
 └─ PlayerController들 (NetworkObject, 인원수만큼 - 오너 한 명에게만 레플리케이트)
```

- `Room`은 게임 타입과 무관하게 항상 같은 하나의 콘크리트 클래스다. `GameType`이
  결정하는 건 Room의 파생 클래스가 아니라 **그 Room이 들고 있을 IGameMode 구현체**다.
- `AllowReconnect`, `FillConnectionWelcome` 등 "게임 규칙"에 해당하는 건
  `IGameMode`가 갖는다. `ExpectedPlayerIds`/`ConnectedClients`(NGO 접속 여부
  추적)처럼 "이 물리적 방에 지금 누가 붙어있나"는 인프라 성격이라 `Room`이
  그대로 갖는다.
- `NetworkObject.Spawn()`은 항상 `Room`이 호출한다 - 방 ID와 소속 플레이어
  목록을 갖고 있어서 가시성(누구한테 레플리케이트할지)을 자연스럽게 판단할 수
  있기 때문. `IGameMode`는 GameState/PlayerController 인스턴스를 만들어서
  반환만 하고, 실제 Spawn은 관여하지 않는다.
- **스폰 순서**(언리얼과 동일): GameMode가 세팅을 끝내고 GameState를 먼저
  만든 뒤, 플레이어가 접속할 때마다 PlayerController를 하나씩 만든다.
- 진행 시점 훅: `IGameMode.OnClientConnected`/`OnClientDisconnected`가
  NetworkManager의 접속/해제 콜백 처리(RoomManager 등록/해제) 직후 호출된다.
  둘 다 최초 접속과 재접속을 구분하지 않고 매번 호출되므로, 구현체가 "이미 이
  playerId용 PlayerController가 있는가"로 스스로 판단해야 한다.
