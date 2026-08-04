# 알려진 문제 (KNOWN_ISSUES)

이 문서는 알려진 버그, 잠재적 위험 요소, 추후 개선이 필요한 사항을 모아둔다.
해결되면 항목을 삭제하지 말고 [해결됨] 표시와 함께 남겨둔다.

---

## [RACE] 매칭 확정 직전 Redis 업데이트 지연

**위치**: `LobbyServer/Services/LobbyControlService.cs` → `RequestMatch()`

**상황**
로비 서버가 Redis 세션을 `Moving`으로 업데이트한 후 `MatchFound`를 전송한다.
`await`으로 순서를 보장하고 있으나, 극도의 부하나 Redis 서버 성능 저하 시
Redis 업데이트 완료 전에 클라이언트가 게임 서버에 접속을 시도할 가능성이 있다.

**증상**
게임 에이전트가 Redis에서 세션을 조회했을 때 아직 `Lobby` 상태로 남아있어
ConnectionApproval에서 접속이 거부될 수 있다.

**현재 상태**
정상 환경에서는 발생하지 않는다. Redis 업데이트가 완료된 후 `MatchFound`를
전송하는 구조(`await` 보장)라 클라이언트가 `MatchFound`를 받는 시점에는
이미 Redis가 `Moving` 상태다.

**잠재적 발생 조건**
- Redis 서버 성능 저하 (고부하, 저사양 기기)
- 네트워크 불안정으로 Redis 응답 지연

**개선 방안 (미구현)**
ConnectionApproval에서 검증 실패 시 즉시 거부하지 않고
짧은 시간(예: 500ms) 재시도 후 거부하는 방식으로 완화 가능.

---

## [해결됨] OnAgentApprovalResponse 중복 Remove 버그

**위치**: `Assets/Scripts/Server/IngameServerBootstrap.cs` → `OnAgentApprovalResponse()`

**상황**
`_pendingApprovals`에서 `TryGetValue`로 값을 꺼낸 후 `Remove`를 했는데,
바로 아래에서 동일한 키로 다시 `TryGetValue`를 시도하는 코드가 있었다.
이미 삭제된 키라 항상 `false`를 반환해서 `pendingResponse.Pending = false`가
호출되지 않아 NGO ConnectionApproval이 영구 대기 상태에 빠졌다.

**증상**
- 게임 에이전트에서 "접속 승인" 로그가 찍혔음에도 NGO 접속이 완료되지 않음
- 클라이언트가 타임아웃으로 끊김

**해결**
중복 `TryGetValue`/`Remove` 제거. 첫 번째 `TryGetValue`에서 꺼낸
`pendingResponse`를 그대로 사용하도록 수정.
