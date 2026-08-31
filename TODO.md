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
- **세션 레지스트리는 "관리 권한 기록부"로만 쓴다. 방 상태는 Redis에 안 둔다.**
  - `room:{roomId}:info`, `room:{roomId}:players` 안 만들기로 결정.
  - 방은 그 방을 열고 있는 인게임 서버와 운명을 같이 한다 - 그 서버가 죽으면 방도 같이 사라지고,
    다른 서버가 이어받는 시나리오 자체가 없다. 그러니 방 상태를 외부(Redis)에 복제해봤자
    "죽은 서버 대신 이어받기"에 못 쓴다 - 애초에 목적이 성립하지 않는다.
  - 유저가 어느 방 소속인지 같은, 그 서버가 스스로 알고 있어야 할 업무 데이터는 각 서버
    내부 메모리에 둔다 (매칭 서버는 `_playerToMatch`로 이미 구현됨. 인게임 서버는 아래
    "방 소속 자료구조" 참고).
- **[완료] 로그인/재접속 흐름은 하나로 통일된다 - "로그인 방식이 2개"로 보지 않는다.**
  JWT를 한 번 발급받으면 클라가 로컬에 캐시해뒀다가 만료 전까지 재사용하는 게 표준이므로:
  - 최초 로그인(또는 토큰 만료) → `Login(id, pw)`로 새 JWT 발급 → `EnterLobby()`
  - 재접속(토큰 아직 유효) → `Login` 생략, 캐시된 JWT로 바로 `EnterLobby()`
  - 서버 입장에서는 `EnterLobby`가 그 JWT가 방금 발급된 건지 캐시에서 꺼낸 건지 몰라도 된다 -
    `AuthInterceptor`가 검증한 playerId만 보고 소유권 클레임을 시도할 뿐이라, "Moving 상태
    로그인 처리"라는 별도 분기가 필요 없어졌다. `TryClaimOwnershipWithPreviousOwnerAsync`가
    생사 여부와 무관하게 이미 일관되게 처리한다(살아있으면 거부+리다이렉트, 죽어있으면 승인).
- 재접속 시 흐름: 클라가 로그인 후 `EnterLobby` RPC를 호출하면, 로비가 세션 레지스트리에
  자신으로 소유권 이전을 시도한다. 기존 소유자(주로 인게임 서버)가 아직 살아있으면 거부되고,
  그 서버 주소가 같이 반환된다 - 로비는 이걸로 "진행 중인 게임이 있습니다" 안내를 클라에게 띄울 수 있다.
  (기존 소유자가 죽어있으면 정상적으로 로비가 소유권을 가져오고 EnterLobby는 성공한다.)
- 서버 생존 TTL과 객체 권한 관리를 Redis(Session Registry)가 담당한다.
  - 서버 하트비트 간격: 5초 / 서버 TTL: 10초
  - 서버 등록과 하트비트를 분리했다. 기동 시 `RegisterServerAsync(id, address)`로
    ID+주소를 한 번 등록하고, 이후 하트비트는 `RefreshServerHeartbeatAsync`가 TTL만 갱신한다
    (값 재전송 없음 - 하트비트에 불필요한 부하를 주지 않기 위함).
  - 권한 이전 판단은 Lua 스크립트로 원자적 처리 (`TryClaimOwnershipAsync` /
    `TryClaimOwnershipWithPreviousOwnerAsync` - 후자는 거부 시 기존 소유자 정보까지 반환).
  - `GetOwnerAsync`(순수 조회), `GetServerAddressAsync` 추가.
  - 클라가 새 서버에 접속 시 TryClaimOwnership으로 권한 이전 요청
- **[완료] MatchToken 제거**: `PlayerSession.MatchToken`, `ingame.proto`의 `ApprovalRequest.match_token`
  모두 제거 (서버/Unity 양쪽 다). 매칭 서버가 게임 에이전트에 플레이어 명단을 전달하므로 불필요.
  ConnectionApproval은 OwnerServerId 검증으로 대체.

**`GameType` 도입 - [완료]**
`allow_reconnect` 필드 하나만 넘기는 대신, 방의 게임 모드 자체(`GameType`)를 파이프라인
전체(로비→매칭→게임에이전트→인게임 서버)에 관통시키기로 결정 변경. 재접속뿐 아니라
게임 룰 전반(랭크/일반/이벤트 모드 등)이 결국 게임 타입에 따라 갈릴 거라, 그 상위 개념
하나로 통일하는 게 확장성이 낫다고 판단.
- `common.proto`에 `GameType` 열거형 신설: `GAME_TYPE_UNSPECIFIED`(필드 안 채움, 구버전
  호환) / `NONE`(의도적으로 모드 없음) / `NORMAL` / `DELETED`(폐지된 모드). `NONE`/`DELETED`를
  미리 넣어둔 이유: 서버들이 전부 롤링 재시작을 마치지 못한 채 배포가 섞이는 경우를 대비 -
  인게임 서버가 모르는 값을 만나면 `GameErrorCode.UnsupportedGameType`으로 명시적 거부하고
  크래시하지 않게 하기 위함.
- `MatchRequest`(클라→로비), `JoinMatchRequest`(로비→매칭), `PrepareMatchRequest`(매칭→
  게임에이전트), `OpenRoom`(게임에이전트→인게임 서버) 전부에 `game_type` 필드 추가, 끝까지
  배선 완료.
- 실제 매칭 시 같은 `game_type`끼리만 묶는 필터링 로직 자체는 아직 없음(매칭 알고리즘이
  여전히 더미라서) - 나중에 실력 기반 매칭 알고리즘 만들 때 같이 구현.

**`allow_reconnect` - [완료] `GameType`으로부터 각 서버가 자체 판단하는 방식으로 구현**

*재접 불가능한 방(GetAllowReconnect(gameType) == false)*
- 클라가 NGO에서 나가면(`OnClientDisconnected`) 게임 에이전트가 **즉시** 세션 레지스트리의
  `OwnerServerId`를 비운다 (기존 동작 그대로).
- 클라가 아무 로비에나 재접속하면 `EnterLobby`가 소유자 없음을 보고 그냥 승인.

*재접 가능한 방(GetAllowReconnect(gameType) == true)*
- `HandlePlayerLeftAsync`(게임 에이전트)가 소유권을 유지한 채로 유예 타이머(기본 30초,
  `ReconnectGracePeriod` 상수)를 돌린다. 그 안에 같은 방으로 재접속하면(`HandlePlayerJoinedAsync`가
  다시 불리면) 세션은 그대로 유지. 유예 시간 안에 재접속 못 하면 `ISessionRegistry.RemoveAsync`로
  세션을 완전히 삭제한다.
- `GetAllowReconnect(GameType)`는 게임 에이전트(.NET, 세션 관리용)와 `RoomManager`(Unity,
  Room 파생 클래스 선택용) **양쪽에 각자 따로** 있다 - 같은 규칙을 중복 구현하는 게 아니라,
  같은 GameType을 보고 "세션을 어떻게 다룰지"(게임 에이전트)와 "방 동작을 어떻게 할지"
  (Unity)라는 서로 다른 결정을 각자 내리는 것이라 의도적으로 분리함.
- 정책값(어느 GameType이 재접속을 허용하는지, 유예 시간 몇 초인지)은 지금 하드코딩 -
  나중에 설정 파일/마스터에서 받아오는 방식으로 바꿀 수 있음(지금 단계엔 충분).

**⚠ 구조적 한계 - 재접속 관련, 없앨 수 없고 완화만 가능**

*재접 불가능 방에서 "나감 처리"와 "재접속 시도"의 경합*
클라가 끊긴 순간부터 서버 정리(Unity→게임에이전트→Redis, 네트워크 몇 홉)가 끝날 때까지
물리적으로 시간이 걸린다. 그 찰나에 클라가 로비에 `EnterLobby`를 부르면, 아직 소유권이
안 풀려서 "기존(재접 불가능한) 인게임 서버로 가라"는 무의미한 안내를 받을 수 있다.
- 이 경합 자체는 클라/서버 어느 쪽에서도 완전히 없앨 수 없다(비동기 이벤트 체인이라
  "정리 완료 전엔 재접속 시도 금지"를 강제할 방법이 없음).
- **클라 쪽에 대기 시간을 두는 건 방어로 쓸 수 없다** - 변조된 클라가 그 대기를 그냥
  무시하고 계속 두드릴 수 있어서, 신뢰 기반 방어는 의미가 없다.
- 근데 이게 보안 구멍은 아니다 - 어차피 재접 거부는 정확히 이뤄지고 있고(뚫리지 않음),
  그냥 짧은 시간 동안 무의미한 왕복이 반복될 뿐이다(자원 낭비 + 어색한 UX 문제).
- **[결정] 이 경합은 처리 불가로 확정 - 완화만 가능.** 세션 레지스트리 반영 전에 클라가
  재접속을 시도하는 것 자체를 막을 방법은 없다(구조적 한계). 레이트 리밋도 이 경합을
  "없애주는" 게 아니라는 걸 명확히 함 - 레이트 리밋은 순수 남용 방지용이고, 반영이
  끝나기 전 재시도는 여전히 정상적으로 거부되며 그건 버그가 아니라 정확한 응답이다.
- **[미구현] `EnterLobby`에 서버 쪽 rate limit 도입** - 업계 조사 결과, 레이트 리밋 시간과
  "세션 반영에 실제로 얼마나 걸리는지"를 맞출 필요가 없다는 걸 확인함(둘은 별개 관심사 -
  레이트 리밋은 순수 남용 방지, 정확성은 매번 최신 상태로 응답하는 실제 로직이 담당).
  그러니 짧고 고정된 쿨다운이면 충분: Redis `SET ratelimit:enterlobby:{playerId} 1 PX 300 NX`
  - NX(없을 때만 성공)+PX(TTL)로 원자적 처리. 로컬 메모리가 아니라 Redis에 두는 이유는
  로비가 여러 대라 로컬에 두면 다른 로비로 우회 가능하기 때문. `ISessionRegistry`(관리
  권한 기록부 전용 원칙)에는 안 얹고, 같은 지연 연결 패턴(`SessionRegistryConnector`)의
  별도 작은 인터페이스로 분리하는 방향.

**방 소속 자료구조 (인게임 서버 쪽) - [완료]**
```csharp
internal abstract class Room
{
    public string RoomId { get; }
    public HashSet<string> ExpectedPlayerIds { get; }             // OpenRoom으로 받은 기대 명단
    public Dictionary<string, ulong> ConnectedClients { get; } = new();  // playerId → clientId, 접속 완료된 것만
    public bool AllowReconnect { get; protected set; }             // 파생 클래스 생성자에서 GameType에 따라 결정
}

internal sealed class NormalRoom : Room { /* AllowReconnect = true (예시 정책) */ }
```
- `RoomManager.OpenRoom(roomId, playerIds, gameType)`이 팩토리 역할 - `GameType`에 따라
  알맞은 `Room` 파생 클래스를 만들고, 모르는/지원 안 하는 타입이면 `GameErrorCode.
  UnsupportedGameType`으로 명시 거부.
- `RoomManager`는 `playerId` 기준으로 관리, `clientId↔playerId` 매핑은 `IngameServerInstance`가
  중개(지금 구조 유지). `IngameServerInstance.OnClientConnected`/`OnClientDisconnected`가
  `RoomManager.RegisterConnectedClient`/`UnregisterConnectedClient`를 호출해서
  `Room.ConnectedClients`를 실시간으로 갱신함.
- 별도의 "아직 방에 배정 안 된 클라" 임시 풀은 안 만듦(그런 시점 자체가 없음).

**[미구현] 게임 에이전트의 자체 방-채움 타임아웃 - 설계 확정**

방 열린 뒤 일정 시간 안에 `ExpectedPlayerIds` 전원이 `ConnectedClients`에 다 들어왔는지
확인하는 타이머. "판단 시점"(타이머 만료)과 "실행 시점"(실제 방 정리) 사이에 상태가
바뀔 수 있는 경합(예: 마지막 결원이 접속하는 순간과 타이머 만료가 겹침)이 있어서,
아래처럼 재검증 단계를 끼워넣기로 함:

**⚠ 별개로 짚은 것 - "게임 시작" 트리거는 타이머랑 완전히 무관해야 함.**
처음 설계엔 "다 찼을 때 뭘 하는지"가 빠져 있어서, 자칫 타이머의 자기 확인 로직에
"다 찼으면 시작"까지 같이 넣기 쉬웠는데 - 그러면 진짜로 다 찼어도 타이머 만료 전까진
게임이 안 시작하는 버그가 생긴다. **"다 찼다"는 마지막 플레이어가 접속하는 바로 그
순간(`RoomManager.RegisterConnectedClient` 안)에 즉시 확인하고 즉시 트리거해야 하고,
타이머는 오직 "그때까지도 안 찼는지"만 확인하는 별개의 진입점이어야 한다.** 타이머가
만료됐는데 이미 다 찼으면(이미 시작 트리거가 먼저 돌았을 것) 조용히 아무것도 안 하고
끝난다.

```
1. 타이머는 Unity Room에 둔다 (Task.Delay - Room이 MonoBehaviour는 아니지만, 메인
   스레드에서 시작한 호출이면 Unity의 SynchronizationContext 덕분에 await 이후도
   자동으로 메인 스레드로 돌아옴 - 코루틴/MonoBehaviour 불필요).
2. 타이머 만료 시 Room이 자기 상태(ConnectedClients vs ExpectedPlayerIds)를 스스로
   확인한다 - RoomManager한테 다시 물어봐도 어차피 Room.ConnectedClients를 그대로
   보는 것이므로(RoomManager.RegisterConnectedClient가 이 Room 인스턴스의 필드를
   직접 갱신함), 중간에 한 번 더 왕복할 이유가 없어서 자체 확인으로 단순화함.
3. 아직도 결원이면 RoomManager한테 "이 방 PendingKill 등록해줘" 요청한다 (이 순간부터
   그 방으로의 신규 접속은 거부 - 이미 처리 중이라고 보고 막음).
4. RoomManager가 게임 에이전트한테 새 메시지로 보고: 인게임 서버 → 게임 에이전트
   방향의 신규 메시지 필요 (`GameServerMessage`에 `RoomTimeout{room_id}` 케이스 추가).
   지금까지 있던 CancelRoom 흐름은 전부 게임에이전트→인게임 한 방향뿐이었는데, 이건
   반대 방향의 신규 배선.
5. 게임 에이전트가 이 보고를 받으면 기존 `CancelRoomAsync`와 같은 처리(매칭 서버한테
   로비 주소 조회 → 세션 이전 → `CloseRoom`으로 최종 정리 지시)를 그대로 재사용한다.
```

**⚠ 남는 경합 - 완전히 없앨 수 없음, `EnterLobby`와 같은 부류의 문제**
"타이머 만료(판단 시점)"와 "실제 처리(실행 시점)" 사이에, 마지막 결원이 NGO 핸드셰이크는
이미 성공했지만 `OnClientConnected` 콜백이 아직 안 불린 그 찰나에 걸리면, 방이 이미
`PendingKill`로 넘어가서 방금 접속한 클라도 리다이렉트 대상으로 취급될 수 있다. 두
이벤트(타이머 만료, NGO 콜백)가 서로 독립적으로 스케줄되는 한 이 창을 0으로 만들
방법은 없다(수학적으로 불가능 - `EnterLobby` 경합과 동일한 성격). 다만 완전히 깨지는
게 아니라 "아깝게 놓친 클라가 정상적으로 리다이렉트 안내를 받는" 정도로 우아하게
처리되므로, 지금은 이 정도로 받아들이고 넘어가기로 함. (창을 더 좁히고 싶으면 나중에
PendingKill 등록 전에 아주 짧은 유예를 한 번 더 두는 것도 검토 가능 - 그래도 0은 안 됨.)

**타이밍 겹침 검토 - 안 겹침을 확인함.** 게임 에이전트가 이미 하는 `CancelRoom` 처리
(매칭 서버가 로비/클라 사정으로 취소를 지시하는 경우)는 **매칭 서버가 아직 가상의 방을
갖고 있는 시점**(6~7번 구간)에만 일어난다. 반면 이번에 새로 만드는 "자체 방-채움
타임아웃"은 **이미 로비가 클라한테 인게임 서버로 가라고 지시한 이후, 매칭 서버가
가상의 방을 지우고 손을 뗀 뒤**에만 일어날 수 있는 시나리오다. 두 경로가 동시에
발생할 수 있는 타이밍 자체가 없어서(매칭 서버가 가상의 방을 아직 안 지웠는데 게임
에이전트가 로비 주소를 요청하는 경우는 순서상 성립 불가), 별도의 경합 방지 로직 없이
`RequestLobbyAddress` 등 기존 배관을 그대로 공유해도 안전함.

### 매칭 흐름 설계 (정상 흐름 + 장애 처리)

**정상 흐름 (최종)**
1. 로비 서버가 매칭 원하는 클라를 매칭 서버에 등록 (`JoinMatch`)
2. 매칭 서버가 클라들을 선별해 가상의 방(matchId) 생성, 선별된 클라 투입
3. 매칭 서버가 적절한 게임 에이전트에게 클라 목록 + 방 ID 제공, 방 생성 요청 (`PrepareMatch`)
4. 게임 에이전트가 요청을 받아 인게임 서버에 전달 (`OpenRoom`)
5. 인게임 서버가 방을 열고 게임 에이전트에 알림 (`RoomOpened`)
6. 게임 에이전트가 매칭 서버에 알림 (`PrepareMatchResult`)
7. 매칭 서버가 방 안 클라들이 속한 로비 서버에게 인게임 서버 주소 전달 (`NotifyMatch`)
8. 로비 서버가 세션 레지스트리에 클라 관리 권한을 해당 인게임 서버로 변경

**[전부 완료] 정상 흐름 1~8번 전부 구현됨.**

**핵심 설계 결정 — 매칭 서버는 8번(권한 이전) 이후 완전히 손을 뗀다.**
초기에는 "게임 에이전트가 플레이어 접속 완료를 매칭 서버에 보고하는 Crossing 단계"를
만들려고 했으나, 여러 차례 논의 끝에 폐기했다. 최종 결론:
- **매칭 서버 책임 범위는 1~7번(로비에 알림을 보내기 전/보내는 시점)까지다.** 알림을 보낸
  뒤에는 손을 뗀다 - 로비도 이 시점부턴 "자기 손을 떠났다"고 보고 아무것도 안 한다
  (로비 관점 표에서 "1~8번: 클라가 넘어간 경우 → 손댈 수 없음"으로 명시).
- **"방 열었는데 아무도(또는 일부만) 안 온다"는 것은 게임 에이전트/인게임 서버가 자체
  타임아웃으로 감지하고 처리한다.** 매칭 서버에 보고할 필요가 없다 - 그래서 "게임
  에이전트 → 매칭 서버 접속 완료 보고" RPC 자체가 불필요해졌다.
- 이 결정 덕분에 `MatchPhase.Crossing`이라는 상태 자체가 필요 없어졌다 (열거형에는
  남아있지만 실제로 쓰이지 않음 - 코드 정리 시 제거 후보).

**매칭 서버 관점 - 각 단계 중단 처리 (전부 구현 완료)**
```
1번(등록만 됨) - 로비 끊김
  → 그 로비가 요청한 매칭 전부 취소 (알림 없음 - 아직 아무것도 알려준 적 없음)

2번(가상 방 생성됨, PrepareMatch 전) - 클라/로비 끊김
  → 방 제거, 생존자 대기열 복귀 (알림 없음)

3번(PrepareMatch 요청~거절 포함) - 클라/로비 끊김 또는 게임 에이전트가 거절
  → (필요시) 게임 에이전트에 방 취소 요청 + 방 제거 + 생존자 복귀 (알림 없음 -
    아직 "잡혔다"고 알린 적 없으므로 조용히 재시도)

6~7번(방 열림 ~ 로비 통보) - 클라/로비/게임에이전트 끊김
  → 게임 에이전트에 방 취소 요청(살아있으면) + 방 제거 + 생존자 복귀 +
    "매칭 취소" 알림 (여기서부터는 이미 로비에 "잡혔다"고 알렸을 수 있으므로 알림 필요)

8번 이후 - 매칭 서버 관할 아님 (게임 에이전트가 직접 처리, 아래 참고)
```
`매치 확정(HandlePrepareMatchResultAsync)`과 `끊김 처리(LeaveMatch/HandleServerDisconnected)`가
같은 matchId를 두고 동시에 경합할 수 있어서(예: 확정되려는 순간 클라가 끊김), 매치의
생성/선점(=제거)을 **`_matchLock` + `TryClaimMatch`로 원자화**했다 - 두 경로 다 이걸
거치므로 먼저 온 쪽만 결과를 결정하고 나머지는 조용히 물러난다("클라 끊김과 같은
의미로 간주"하기로 결정). `_matchesByLobby`(로비ID→matchId 역인덱스)도 같은 락으로
보호해서, 로비 여러 개가 동시에 죽어도 순서대로 직렬화된다.
`PendingMatch.Players`(`MatchPlayer` 리스트)가 생성 시점에 로비 정보를 스냅샷으로
들고 있어서, 매치 관련 처리 중에 `_playerPool`을 다시 조회할 필요가 없다 - 이 덕분에
매치로 넘어간 플레이어는 곧바로 `_playerPool`에서 빠지고, 중단되면 `DisruptMatch`가
명시적으로 "복귀"시킨다.

**로비 서버 관점 (구현 완료)**
```
클라 접속 시
  세션 레지스트리에 권한 확인
    등록된 클라, 권한 아님 → 권한 보유 서버로 이동 요청 (EnterLobby가 처리)
    미등록 클라 → 권한 취득 시도, 실패 시 접속 끊음
1~8번
  클라가 남은 경우 → 대응 불필요
  클라가 넘어간 경우 → 손을 뗌
  클라가 끊긴 경우 → 매칭 서버한테 LeaveMatch 요청 (구현 완료 - CancellationToken 출처로
    "정상 종료 vs 연결 끊김"을 구분해서 후자일 때만 호출)
```

**게임 에이전트 관점**
```
방 생성 요청이 온 경우 → 방 생성 [완료]
방 취소 요청이 온 경우(CancelRoom) [완료]
  접속한 클라가 존재
    매칭 서버한테 로비 주소 요청(RequestLobbyAddress)
      존재 → 세션 권한 이전 + CloseRoom(리다이렉트 정보 포함)으로 게임 서버에 전달
      부재 → CloseRoom(리다이렉트 없음)
  방 제거
요청에 없는 클라가 접속한 경우 → 접속 끊음
  (OwnerServerId 기반 ConnectionApproval이 이미 처리 - 별도 구현 불필요)

[미구현] 8번: 방 열린 뒤 일정 시간 대기 → 전원 접속했는지 자체 확인
  클라가 방에 모두 접속 → 게임 시작
  방에 결원 존재 → 매칭 서버한테 로비 주소 요청 → (위 CancelRoom과 같은 처리)
  ※ 이 "일정 시간 대기 후 결원 체크" 타이머 자체가 아직 게임 에이전트에 없다.
    GameServerHandler가 PrepareMatch로 받은 기대 플레이어 목록과 실제 PlayerJoined
    수신 현황을 비교하는 로직 + 타이머 신설 필요. 지금 구현된 CancelRoom 처리는
    "매칭 서버가 먼저 취소를 요청한 경우"에만 반응하는 수동적인 경로다.
```

**"일부만 접속 실패 시 방 전체 취소" 정책 - 의도적 결정**
리그 오브 레전드처럼 결원 있는 채로 시작하는 방식도 있지만, 여기서는 공정성을 우선해서
전원 취소로 결정. "로비가 죽어서 이동 지시를 못 받은 것"도 결과적으로 "결원 발생"이므로
같은 정책(전체 취소)을 적용한다.

**클라 → 새 로비 리다이렉트 [구현 완료]**
클라가 매치 확정 시(`MatchFound`) 원래 로비 스트림을 이미 닫아버리므로(NGO 접속 직전),
방 취소 시점엔 로비를 통해 직접 push할 채널이 없다.
- **이미 NGO로 접속한 클라**: `ingame.proto`의 `CloseRoom`/`PlayerRedirect` 메시지로
  게임 에이전트 → 인게임 서버 → (`NetworkManager.DisconnectClient(clientId, lobbyAddress)`)
  순으로 리다이렉트 주소를 실어서 끊는다.
- **접속 시도 중인 클라**: `ConnectionApprovalCallback` 시점에 방이 이미 없으면 그 자체가
  거부 사유가 된다 (별도 로직 불필요).
- 리다이렉트 주소를 못 찾은 클라는 그냥 끊고, 클라가 스스로 로비 재접속(`EnterLobby`)을
  타면 소유권 충돌 검사가 알아서 올바른 곳으로 안내한다 (매칭 서버가 특정 로비를
  지정 배정/재시도하는 로직 불필요).

**신규 RPC/메시지 [구현 완료]**
- `matching.proto`: `CancelRoom`(매칭→게임에이전트), `RequestLobbyAddress`(게임에이전트→매칭)
- `ingame.proto`: `CloseRoom`/`PlayerRedirect`(게임에이전트→인게임 서버)
- `MatchingServer/Services/IngameMatchService.cs` → `InGameRelayService.cs`로 개명
  (게임 에이전트가 매칭 서버에 거는 요청을 받는 쪽이라는 의미를 이름에 반영.
  `MatchCallbackClient`(매칭이 게임 에이전트한테 거는 쪽)와 대칭됨)

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
  [완료] 해당 로비 소속 클라 전부 방/클라 리스트에서 제거 (MatchEngine.HandleServerDisconnected,
    _matchesByLobby 인덱스로 O(1)에 가깝게 조회)

매칭 완료 이후 - 매칭 서버 관할 아님 (게임 에이전트가 직접 처리)

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

- [완료] 퇴장 후 로비 복귀 검증 테스트 - 오늘 클라 접속→매치→인게임 이동→게임 종료→
  로비 복귀(리다이렉트+재접속)까지 실제 로그로 전부 확인함.

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

- **매칭 서버 - 배관 전부 완료, 남은 건 실력 기반 알고리즘뿐**
  - [완료] 접속 유지 스트림, 매칭↔게임에이전트 전체 RPC, 매칭 확정/취소 통보, 실제 방
    생성/대기, 접속 끊김/서버 다운 처리 전체(`CompensatingTransaction`/`_matchLock`),
    클라 리다이렉트, 클래스 개명(`InGameRelayService`), "Crossing 단계" 폐기 결정,
    게임 에이전트 자체 방-채움 타임아웃(`RoomTimeout`/`PendingKill`), `EnterLobby` rate
    limit, 매치 정상 종료→로비 복귀(`CloseRoomAndRedirectAsync`), 더미 인게임 로직
    (`IGameLogic`/`DummyGameLogic`) - 전부 오늘까지 완료 및 실제 테스트로 검증됨.
  - **[미구현] 실력 점수 기반 매칭 알고리즘** - 지금은 더미(1명이면 즉시 매칭). 후순위로
    미루기로 함 (아직 게임 자체가 없어서 뭘 기준으로 매칭할지 정할 수 없음). 이때
    `JoinMatchRequest`/`WaitingPlayer`에 이미 있는 `game_type`별로 대기열을 나누는
    필터링도 같이 구현 필요(지금은 안 나뉘고 있음).

- **로그인 서버(`LoginServer`/`AccountServer`) - 배관 완료, 검증 로직만 남음**
  - [완료] 계정 검증/생성, 세션 레지스트리 권한 획득, 매칭 서버 로비 조회, 소유권 자발적
    이관, 클라 최초 진입 전체 흐름. Unity `ClientInstance` 연동까지 완료(오늘 실제 빌드/
    테스트 통과).
  - **[미구현] 토큰 2슬롯(`CurrentToken`/`PreviousToken`) 실제 대조 검증** - `PlayerSession`에
    자료구조는 있지만, "제출된 토큰을 세션의 2슬롯과 TTL로 대조해서 거부"하는 로직 자체가
    없음. 검증 지점은 이동 방향에 따라 다름:
    - **로비→인게임**: 게임 에이전트의 `ConnectionApproval`이 검증해야 함(서버 간 소통을
      게임 에이전트가 대신하므로). NGO 접속 payload에 토큰 추가, `ingame.proto`의
      `ApprovalRequest`에 필드 추가, 게임 에이전트 검증 로직 신설 필요.
    - **인게임→로비**: 로비의 `EnterLobby`가 검증해야 함. 지금은 무조건 새로 발급만 하고
      대조는 안 함.
    - 검증 실패 시 정책(조용히 통과 vs 실제 거부)도 미결.

---

## 구현 완료

- [완료] **로그인 서버(`LoginServer`) + 계정 서버(`AccountServer`) 신설**
  - `common.proto`에 `ServerType.LOGIN`/`ServerType.ACCOUNT` 추가.
  - `master.proto`: `AccountServerInfo` 신설. **의도적으로 매칭 서버 주소랑 같은
    "필드별 명시적 하드코딩" 방식** - 범용 `Dictionary<ServerType, string>`/`InfrastructureAddress`
    같은 통합 구조로 안 감. 이유: 통합하면 "이 서버 주소를 빠뜨려도 컴파일이 통과하는"
    문제가 생기는데, 각 인프라마다 전용 필드를 두면 필수 주소 빠뜨림을 스키마 차원에서
    방지할 수 있음(코드 리뷰/컴파일 단계에서 놓치기 어려워짐). `MasterConnectionService`/
    `ConnectionHandler`에 매칭 서버와 완전히 같은 패턴으로 배선 완료.
  - **`AccountServer`**: 계정 검증/생성 전담, 단항 RPC만(매칭 서버와 달리 서버 풀 관리가
    아니라 순수 조회/생성 저장소라 접속 유지 스트림 불필요). `IAccountRepository` +
    `InMemoryAccountRepository`(`AccountServer/Debug/`, 아직 SQL 없음) 패턴. `playerId`는
    계정 생성 시 GUID 발급, 충돌 방어용 재발급 루프(최대 5회 - 실질적으로 거의 발생 안 함).
  - **`LoginServer`**: 클라 최초 진입점 전용, 스트림 기반(`MasterControl.Connect`와 같은
    패턴 - "일정 시간 안 제출 안 하면 끊는다"를 구현하려면 단항 RPC로는 안 됨). 흐름:
    접속 → 계정 정보 제출 대기(30초 타임아웃) → AccountServer 대조/생성 → 세션 레지스트리
    관리 권한 획득 → 매칭 서버에 로비 주소 요청 → 성공 시 소유권 자발적 이관(게임 에이전트의
    `CloseRoomAndRedirectAsync`와 같은 패턴) + 클라에 리다이렉트 지시 → 스트림 종료.
    **JWT/재접속 개념이 아예 없음** - 로그인 성공한 클라는 이 서버로 다시 안 오고,
    연결이 끊기면 어느 단계에서든 그냥 끊긴 걸로 끝(복구/재시도 로직 없음).
  - 매칭 서버가 갈 수 있는 로비를 못 찾으면 `LoginRejected{reason="NO_LOBBY_AVAILABLE"}`
    로 거부 - "서버가 가득 찼습니다" 케이스.
  - [완료] **Unity 클라 쪽 연동** - `ILoginChannel`/`GrpcLoginChannel` 신설, `ClientInstance.Start()`가
    로그인 서버 경유하도록 재작성 완료. `LoginServer.Contracts.csproj`에 Unity 복사 빌드
    타겟 누락돼있던 것도 발견해서 추가함.
  - [완료] **전체 엔드투엔드 테스트 성공** - 클라 로그인(계정 생성) → 로비 주소 수신 →
    EnterLobby → RequestMatch → 매치 확정 → NGO 인게임 접속 → 게임 진행(더미) → 방 종료
    지시 → 클라 자진 이탈 → 새 로비 재접속까지 로그로 전부 확인함.
  - **[해결됨] `HTTP_1_1_REQUIRED` 오류** - `LoginConnectionHandler`가 `AccountServer`/
    매칭 서버를 단항 RPC로 호출할 때 평문(TLS 없음) HTTP/2에서 이 오류가 재현됐다.
    `GrpcChannel.ForAddress(address)`(기본 핸들러)만 쓰면 실패하고, `MasterConnectionService`가
    쓰는 것과 동일하게 `SocketsHttpHandler`를 명시적으로 구성해서 `GrpcChannelOptions.HttpHandler`에
    넘기면 해결됨 - 정확한 내부 메커니즘은 규명 못 했지만(같은 프로세스 내 다른 단항 호출은
    기본 핸들러로도 잘 되는 사례가 있어서 원인이 더 미묘함), 재현 가능한 해법은 확보함.
    **비슷한 오류가 다른 곳에서도 나면 이 패턴(`CreateChannel` 헬퍼, `LoginConnectionHandler.cs`
    참고)을 우선 적용해볼 것.**
    (참고: 도중에 Kestrel을 `Http1AndHttp2`로 완화해봤는데 이건 답이 아니었음 - TLS 없는
    환경에서 `Http1AndHttp2`는 ALPN 협상 채널이 없어서 Kestrel이 그냥 HTTP/1.1로 확정해버림.
    `Http2` 단독이 h2c로 정확히 동작하는 올바른 설정이었고, 그대로 유지함.)
  - **[확인 필요]** 이번 스캐폴딩(csproj, appsettings.json, `.slnx`)은 이번 세션 스크래치
    공간 리셋으로 정확한 로컬 원본이 없어서, 다른 메인 서버 프로젝트 패턴을 참고해
    재구성함. 로컬의 다른 프로젝트 설정과 다른 부분 없는지 확인 필요.
  - **포트 배정**: `AccountServer` 5500/5501, `LoginServer` 5400/5401 (`DummySubServer`가
    이미 5300을 쓰고 있어서 처음 배정했던 5300과 충돌났던 적 있음 - 새 서버 포트 잡을 땐
    `start-servers.ps1`/기존 서버들 전체와 대조 필수). `start-servers.ps1`/`stop-servers.ps1`에
    두 서버 추가 완료 (마스터→매칭 다음, DummySubServer 이전 순서).
  - **[미구현]** 실제로 "제출된 토큰을 세션의 2슬롯과 대조"하는 검증 로직 - 여전히
    미구현 상태(이전 항목과 동일). `EnterLobby`/`ConnectionApproval` 양쪽 다.


- [완료] **인증 체계 임시 개편 (3번 결정) + 세션 레지스트리 토큰 2슬롯 (1번 결정)**
  - `Login`/`CreateAccount`를 코드에서 완전히 제거 (별도 "로그인 서버"로 분리하는 게
    맞는 방향이지만 그건 나중 작업 - `TODO`에 미결로 남김). `EnterLobby`부터 토큰이
    필요한 임시 상태가 됨.
  - `DummyTokenProvider` 단순화: 어떤 문자열이든("1"이든 "12312"든) 그대로 playerId로
    취급하고 통과시킴. `GenerateToken`도 `playerId`를 그대로 반환 - 발급/검증 둘 다
    "토큰 = playerId"로 일관.
  - `PlayerSession`에 `CurrentToken`/`PreviousToken`(2슬롯) + `TokenIssuedAtUnixMs`
    (TTL 판단용) 필드 추가. 갱신 권한은 `OwnerServerId`를 가진 서버만.
  - `EnterLobby`가 성공할 때마다 기존 `CurrentToken`을 `PreviousToken`으로 밀어내고
    새 토큰을 발급, `EnterLobbyResponse.token`으로 돌려줌.
  - 클라(`ClientInstance`)는 최초 접속 시 스스로 만든 토큰(`"test_player"` 그대로)을
    `LobbyClient.SetToken`으로 주입해 `EnterLobby`를 부르고, 성공 시 받은 토큰을
    `_cachedToken`에 저장 - 이 필드는 `LobbyClient`/`GrpcLobbyChannel`이 게임 세션마다
    Dispose되고 새로 만들어져도 살아남아서, 게임 종료 후 새 로비로 돌아갈 때 재사용됨.
  - **[미구현]** 실제로 "제출된 토큰을 세션의 2슬롯과 TTL로 대조해서 거부"하는 로직
    자체는 없음. 검증 지점은 방향에 따라 다름 - **로비→인게임**은 게임 에이전트의
    `ConnectionApproval`이(서버 간 소통 대신하므로), **인게임→로비**는 로비의
    `EnterLobby`가. 지금은 둘 다 이 대조를 안 하고 무조건 통과/재발급함 - 재접속
    정책 전체를 다룰 때 같이 구현 예정.

- [완료] **게임 종료 후 클라 로비 복귀 - 실제 재접속까지 확인**
  - `ClientInstance.OnRoomClosedMessageReceived`가 `RoomClosed` 메시지 받으면: 확인
    응답 전송 → **서버 응답을 안 기다리고 즉시 스스로 NGO 연결 종료** → 리다이렉트
    주소가 있으면 `ReconnectToLobbyAsync`로 새 로비에 `EnterLobby`까지 자동 시도.
  - `ARCHITECTURE.md`에 "클라 자진 이탈 vs 서버 강제 종료" 경합 기록함 - 실질적
    위험은 없음(`IngameServerInstance._clients`의 `ConcurrentDictionary` 원자성으로
    이미 방어됨, `Update()`의 `RedirectDisconnect` 처리에 `ContainsKey` 가드 추가).
  - `LobbyState.LoggedIn` 제거 (Login이 없어져서 `Connected`에서 바로 `EnterLobby`
    가능해짐).


- [완료] **디버그/더미 코드 한 자리에 모으기 컨벤션 확립**
  - Unity: `AutoBattler.Game.Dummy` 네임스페이스(`Server/Game/Dummy/`) - `Debug`란 이름은
    `UnityEngine.Debug`랑 충돌해서 `Dummy`로 채택. `IGameLogic`(인터페이스, `Server/Game/`)
    + `DummyGameLogic`(구현체, `Dummy/`) 완료 - "방 전원 접속 완료" 시 `RoomManager`가
    `GameManager.NotifyMatchStart`를 통해 트리거, 15초 뒤 더미 결과를 만들어
    `IngameServerInstance.ReportMatchResult` → `SendMatchResultAsync`로 흘려보냄. 이게
    "클라 접속→매치 시작→인게임 이동→게임 종료→로비 복귀" 전체 배관을 실제로 태우는
    최소 구현.
  - .NET: 프로젝트마다 `Debug/` 폴더로 통일 (프로젝트 자체가 분리 배포 단위라 전체
    솔루션에서 물리적으로 한 폴더에 모을 순 없지만, 이름 규칙을 통일해서 grep 한 번으로
    전부 찾을 수 있게 함). `DummyTokenProvider`(`LobbyServer.Auth`→`LobbyServer.Debug`),
    `DummyServerAuthenticator`(`MasterServer.Security`→`MasterServer.Debug`),
    `DummyChatFilter`(ChatServer는 프로젝트 전체가 네임스페이스 없는 관례라 폴더만 이동)
    전부 이동 완료. 인터페이스(`ITokenProvider`, `IServerAuthenticator`, `IChatFilter`)는
    원래 위치에 그대로 둠 - 나중에 진짜 구현체로 교체할 때 인터페이스는 안 건드리고
    `Debug/` 파일만 교체/삭제하면 되게.
  - `DummySubServer`(프로젝트 자체)는 대상에서 제외 - 프로젝트 전체가 이미 더미 전용이라
    내부에 "진짜 코드 사이에 섞인 더미"가 없음.


- [완료] 죽은 코드 정리: 마스터 서버의 `RequestIngameAssignment` RPC 전체 제거
  (`master.proto`의 RPC/메시지, `MasterControlService.RequestIngameAssignment`,
  `RoutingHandler.cs`, `Routing/IRoutingStrategy.cs`, `Routing/LeastLoadRoutingStrategy.cs`
  전부 삭제. 딸려있던 `InstanceRegistry.GetFreshByType`/`InstanceInfo.LastHeartbeatUtc`도
  이거 하나 때문에만 존재했어서 같이 제거) - 매칭 서버가 생기면서 라우팅 방식이
  `PrepareMatch`(매칭→게임에이전트)로 완전히 대체됐는데 안 지워지고 남아있던 것.

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
