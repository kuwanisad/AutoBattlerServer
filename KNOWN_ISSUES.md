# KNOWN_ISSUES

발견해서 이미 고친 문제들을 "다시 같은 실수를 안 하기 위해" 기록해두는 문서.
`ARCHITECTURE.md`가 "지금 구조가 어떤지"를 설명한다면, 이 문서는 "어떤 문제를 어떻게
찾아서 어떻게 고쳤는지"를 남긴다 - 나중에 비슷한 증상을 겪으면 여기부터 찾아볼 것.

각 항목은 **발견 - 원인 - 예상 피해 - 해결 방법 - 결과** 순서로 정리한다.

---

## 1. AccountServer 호출 시 원인 불명의 `HTTP_1_1_REQUIRED` 오류

- **발견**: 로그인 서버가 계정 서버를 호출할 때마다 `Grpc.Core.RpcException`으로
  `HTTP_1_1_REQUIRED` 오류가 재현됨. 같은 패턴(`GrpcChannel.ForAddress`, Kestrel `Http2`
  단독 포트)을 쓰는 다른 호출(매칭↔게임에이전트 등)은 멀쩡히 동작해서 원인 특정이 어려웠음.
- **원인**: 정확한 내부 메커니즘은 끝내 규명 못 함. 다만 `MasterConnectionService`가
  마스터 서버를 호출할 때 `SocketsHttpHandler`를 명시적으로 구성해서 쓰고 있었는데,
  `LoginConnectionHandler`는 `GrpcChannel.ForAddress(address)`만 쓰고 있었다는 차이가 있었음.
- **예상 피해**: 로그인 서버가 계정 검증/생성을 아예 못 해서 로그인 자체가 불가능.
- **해결 방법**: `MasterConnectionService`가 쓰는 것과 동일한 `SocketsHttpHandler` 구성
  (`EnableMultipleHttp2Connections`, `KeepAlivePingDelay`/`KeepAlivePingTimeout`/
  `KeepAlivePingPolicy`)으로 채널을 만드는 `CreateChannel` 헬퍼로 교체.
  (참고: 중간에 Kestrel을 `Http1AndHttp2`로 완화해본 적 있는데 이건 오답이었음 - TLS
  없는 환경에서 `Http1AndHttp2`는 ALPN 협상 채널이 없어서 그냥 HTTP/1.1로 확정돼버림.)
- **결과**: 해결됨. 비슷한 오류가 다른 곳에서도 나면 이 패턴(`LoginConnectionHandler.cs`의
  `CreateChannel` 참고)을 먼저 적용해볼 것.

---

## 2. 토큰이 playerId 그 자체라서 2슬롯 검증이 무의미했음

- **발견**: 토큰 2슬롯(`CurrentToken`/`PreviousToken`) 대조 검증을 실제로 붙이려고 보니,
  `DummyTokenProvider.GenerateToken`이 그냥 `playerId`를 그대로 돌려주고 있었음.
- **원인**: 최초 설계 당시 "로그인 서버는 나중, 지금은 임시로"라는 방침으로 토큰=playerId로
  단순화해뒀던 것.
- **예상 피해**: 남의 playerId만 알면(또는 추측하면) 그걸 그대로 토큰으로 제출해서 통과됨 -
  대조 검증 로직을 아무리 정교하게 만들어도 실질적 보안 효과가 0.
- **해결 방법**: `DummyTokenProvider`를 `"{무작위값}:{playerId}"` 형식으로 변경 - 매 발급마다
  값이 달라지게 함. `AuthInterceptor`가 여전히 파싱만으로 playerId를 꺼낼 수 있으면서도
  회전이 실제로 의미를 갖게 됨.
- **결과**: 해결됨. `Shared.Common/Auth/Debug/DummyTokenProvider.cs`.

---

## 3. `ConnectionApproval`(로비→인게임)에 토큰 검증이 아예 없었음

- **발견**: 토큰 2슬롯 검증을 EnterLobby(인게임→로비)에 먼저 붙이고 나서, "인게임 서버
  쪽(로비→인게임)도 됐나?"라는 확인 질문에서 발견. `ApprovalRequest`에 애초에 토큰 필드
  자체가 없었음(`player_id`만 있었음).
- **원인**: EnterLobby 쪽 작업 범위를 명시적으로 좁혀서 먼저 끝낸 뒤, ConnectionApproval
  쪽은 "다음 작업"으로 미뤄뒀던 것이 그대로 남아있었음.
- **예상 피해**: 로비→인게임 이동은 `OwnerServerId`만 확인하고 진짜 본인인지(토큰)는 전혀
  검증 안 함 - EnterLobby 쪽만 뚫어놓고 정작 게임 서버 진입은 여전히 열려있는 상태.
- **해결 방법**: `ingame.proto`의 `ApprovalRequest`/`ApprovalResponse`에 각각 `token`/
  `new_token` 필드 추가. NGO 접속 payload를 `"{playerId}|{token}"` 형식으로 변경. 게임
  에이전트가 `TokenVerification.IsValid`로 검증 후, 승인 시 토큰 회전까지 수행. 새 토큰은
  `ApprovalResponse`(게임 서버↔게임 에이전트 간 통신이라 클라가 직접 못 받음) 대신
  `CustomMessagingManager`로 `"TokenUpdate"` 메시지를 신설해서 클라에 별도 전달.
- **결과**: 해결됨. `EnterLobby`/`ConnectionApproval` 양쪽 다 같은 `TokenVerification`
  공용 로직을 재사용.

---

## 4. 로비에 조용히 앉아있는 클라의 끊김을 감지할 방법이 없었음

- **발견**: "메인 서버와 접속이 끊긴 클라는 채팅 서버와도 자동으로 끊기나?"라는 검증
  질문에서, `SessionInvalidated` 발행 지점을 전수 조사하다가 발견.
- **원인**: `SessionInvalidated` 발행이 명시적 로그아웃과 인게임 재접속 유예 타임아웃,
  딱 두 곳에서만 일어남. 로비는 클라와 지속 연결이 없어서(단항 RPC만 오감) "클라가
  아직 여기 있는지" 자체를 감지할 방법이 없었음. 세션 TTL(24시간)도 이 용도가 아니었음
  (5번 항목 참고).
- **예상 피해**: 로비에 있던 클라가 와이파이가 영구히 끊겨도, 로비는 그 사실을 몰라서
  세션을 계속 붙들고 있음 - 채팅 서버 연결도 안 끊김(최대 24시간). 그 플레이어는 다른
  기기로 재로그인도 못 함(세션 충돌로 거부당함).
- **해결 방법**: `StayConnected` 스트림 RPC 신설 - 클라가 EnterLobby 성공 직후 열고,
  로비에 있는 내내 유지. 내용은 안 오가고 "연결이 살아있다는 사실 자체"가 생존 증거.
  스트림이 끊기면 `OwnerServerId == 나` 그리고 `Status == Lobby`(정상 이동 중이 아님)일
  때만 세션 삭제 + `SessionInvalidated` 발행.
- **결과**: 해결됨. 인게임은 이미 NGO 지속 연결로 같은 효과를 얻고 있었으므로 로비만
  추가하면 됐음.

---

## 5. 세션 TTL(24시간)의 실제 역할을 서로 다르게 이해하고 있었음 → 결국 완전 제거

- **발견**: 4번 문제를 고치려다 "TTL이 왜 24시간이냐"는 질문에서 시작된 논의. 세션
  TTL(`session:{playerId}`)과 서버 생존 TTL(`server:{serverId}`)을 혼동하고 있었음이
  드러남.
- **원인**: `session:{playerId}` 키의 TTL은 `TryClaimOwnershipAsync`/`UpdateAsync`가 쓸
  때 한 번만 설정되고 갱신되는 일이 없었음 - "정상 정리 실패 시의 최후 안전망"일 뿐,
  개별 세션 생존을 능동적으로 감지하는 용도가 아니었음. 반면 `server:{serverId}` 키는
  `ServerHeartbeatService`가 지속 갱신하는 진짜 생존 신고였음.
- **예상 피해**: 이 둘을 혼동한 채로 "세션 TTL을 짧게 줄여서 생존 감지에 쓰자"는 식으로
  잘못 갔다면, 정상적으로 오래 로비에 머무는 플레이어의 세션이 주기적 갱신 없이 그냥
  만료돼버렸을 것.
- **해결 방법**: 개별 세션 생존 감지는 4번(`StayConnected`)으로 완전히 대체하고,
  `TryClaimOwnershipScript`(Lua)의 "소유자가 죽었는지" 판단은 원래부터 그랬듯
  `server:{serverId}`의 TTL만 본다는 걸 재확인. `session:{playerId}` 자체의 TTL은
  실질적 효과가 없다고 결론 내리고 완전히 제거(`RedisSessionRegistry`의 `UpdateAsync`/
  `TryClaimOwnershipWithPreviousOwnerAsync`에서 `EX` 옵션 제거).
- **결과**: 해결됨. 남는 좁은 경우(서버가 죽은 뒤 그 플레이어가 다시는 안 돌아오면 세션이
  무기한 남음)는 감수하기로 함 - 재접속 자체가 없으면 재판단 계기도 없어서 실질적 영향
  미미.

---

## 6. `StayConnected` 스트림 끊김과 매치 확정이 겹치는 race

- **발견**: 4번 해결책을 설계하는 도중, "스트림 끊기면 무조건 세션 삭제"로 가면 매치
  확정으로 정상 이동 중인 클라도 같이 삭제될 수 있다는 지적.
- **원인**: 매치가 확정되면 클라가 `MatchFound`를 받고 로비 채널(과 그 안의
  `StayConnected` 스트림)을 스스로 정리하는데, 그 타이밍이 "스트림이 끊겼다 = 클라가
  사라졌다"는 판단과 시간상 겹칠 수 있음.
- **예상 피해**: 매치 확정으로 인게임 이동 중인 클라의 세션이 로비 쪽에서 삭제돼서,
  인게임 서버의 `ConnectionApproval`이 그 세션을 못 찾고 접속을 거부.
- **해결 방법**: 스트림 종료 시 `OwnerServerId`뿐 아니라 `session.Status`도 같이 확인.
  `MatchNotificationDispatcher`가 매치 확정 시 클라에게 `MatchFound`를 보내기 **전에**
  이미 `OwnerServerId`를 인게임 서버로, `Status`를 `Moving`으로 갱신해두므로, 정상
  이동이면 스트림 종료 핸들러가 도달했을 때 이미 조건에 안 맞아 자동으로 스킵됨.
- **결과**: 해결됨. `LobbyControlService.HandleStayConnectedEndedAsync`.

---

## 7. `SessionInvalidated` 발행-소비 사이의 지연으로 생기는 race

- **발견**: "로비 끊김→세션 삭제→채팅 서버 재접속 시도" 순서를 검증하는 질문에서 발견.
- **원인**: `RemoveAsync`(Redis 쓰기)와 `PublishSessionInvalidated`(RabbitMQ 발행) 사이,
  그리고 발행부터 채팅 서버가 실제로 소비하기까지는 시간차가 있음. 그 사이에 채팅 서버가
  (아직 안 지워진) 세션을 보고 재접속을 받아줄 수 있음.
- **예상 피해**: 이미 무효화된 세션으로 채팅 서버에 접속한 클라가 계속 남아있게 될 위험.
- **해결 방법**: 별도 조치 없이 기존 순차 처리 흐름을 그대로 신뢰하기로 함 -
  `RemoveAsync`와 `PublishSessionInvalidated`가 **같은 메서드 안에서 연달아** 실행되므로,
  설령 채팅 서버가 그 틈에 재접속을 받아줘도 뒤이어 `SessionInvalidatedSubscriber`가
  그 연결을 다시 끊어버림. "영구 잔류"가 아니라 "잠깐 살아있다가 뒤늦게 정리"로 귀결.
- **결과**: 감수하기로 함(완전히 없앨 수 없는 구조적 지연, 해가 크지 않음). 더 엄격하게
  막으려면 재접속 검증에 락을 걸거나 로비 정리 완료를 기다리는 식으로 가야 하는데,
  지금은 그 정도까진 필요 없다고 판단.

---

## 8. 메시지 전송 권한 확인과 실제 전달/저장 사이의 race (친구 삭제와 메시지 전송이 겹침)

- **발견**: "클라1이 클라2를 친구에서 지웠는데 그 직후 클라2가 메시지를 보내면 어떻게
  되나?"라는 검증 질문에서 발견.
- **원인**: `HandleSendAsync`가 `GetOtherMemberAsync`로 방이 살아있는지 확인한 시점과,
  실제로 메시지를 저장/전달하는 시점 사이에 아주 짧은 틈이 있음 - 그 틈에 방이 삭제되면
  이미 통과한 메시지 한 통은 그대로 전달/저장될 수 있음.
- **예상 피해**: 친구를 지웠는데도 그 직후 메시지 한 통이 새어 들어올 수 있음.
- **해결 방법**: 서버 쪽엔 별도 락을 안 걸기로 함(구조가 무거워짐, 확률도 낮음). 대신
  클라(`WebSocketChatChannel`) 쪽에서 방어 - `RoomRemoved` 통보를 받은 채널은 그 이후
  새는 메시지를 조용히 버리는 "차단 목록" 방식 채택. 처음엔 "허용 목록"(내가 아는 유효
  채널만 통과)으로 가려다가, 그러면 `GetMyRoomsAsync`를 아직 안 부른 접속 초반에 정상
  메시지까지 잘못 걸러질 위험이 있어서 뒤집음.
- **결과**: 해결됨(완전히는 아니고 창을 좁힘). 클라1이 직접 지운 경우는 자기 로컬 목록을
  즉시 갱신하면 되므로 race 자체가 없음 - 문제가 되는 건 클라2가 지운 걸 클라1이 뒤늦게
  아는 경우뿐, 그마저도 `RoomRemoved` 알림으로 곧 정리됨.

---

## 9. `sender_id`를 빈 값으로 "시스템 메시지" 특수 취급하려다 발견한 스푸핑 위험

- **발견**: 시스템 안내 메시지("잠시 후 채팅쳐 주세요" 등)를 어떻게 필터에서 예외
  처리할지 설계하던 중, "빈 sender_id = 시스템 메시지"라는 규약을 제안했다가 지적받음.
- **원인**: "신원"(sender_id)이라는 필드에 "신뢰해도 되는가"라는 완전히 다른 의미를
  암묵적으로 얹으려 했음. 지금 서버 코드에서는 `sender_id`가 항상 인증된 연결의
  playerId라 당장 뚫을 방법은 없었지만, 나중에 리팩터/새 기능(익명 메시지 등)으로
  `sender_id`가 정상적으로 비게 되는 경로가 생기면, 그 순간부터 클라가 그걸 전부
  "신뢰할 시스템 메시지"로 오인하게 됨.
- **예상 피해**: 신원 필드의 특수값에 신뢰 판단을 의존하는 설계 자체가 훗날의 버그/기능
  추가로 조용히 뚫릴 수 있는 잠재적 취약점.
- **해결 방법**: 업계 사례(라이엇 게임즈 채팅 아키텍처) 조사 결과, 스푸핑 방지는 원래
  서버가 메시지를 받는 시점에 전부 끝내고 받는 클라는 그냥 믿기만 하면 되는 구조가
  일반적이었음 - 저희 구조도 이미 그랬음(클라가 SendMessageRequest로 sender_id를 못
  정함). 진짜 문제였던 "빈 값 특수 취급"은 `SystemChatDelivery`라는 완전히 별개의
  `oneof` 케이스로 분리해서 해결 - 클라가 정상 Send로는 절대 만들어낼 수 없는 타입이라,
  필드 값 해석이 아니라 타입 자체가 신뢰의 근거가 되도록 바꿈. 그 안에 다시 `oneof`
  (`ChatNotice`/`GlobalNotice`)를 둬서 "특정 방에 종속된 안내"와 "방과 무관한 전역
  안내"도 구조적으로 나눔.
- **결과**: 해결됨. `chat.proto`의 `SystemChatDelivery`, `WebSocketChatChannel.HandleMessage`.

---

## 10. `WebSocketHandler`에 인증이 아예 없었음(구버전 유산)

- **발견**: 채팅 서버 재설계를 시작하며 기존 파일을 검토하던 중 발견. 코드에 이미
  `TODO: JWT 토큰 검증으로 교체`라고 적혀있었음.
- **원인**: 예전 버전에서 `playerId`를 그냥 URL 쿼리 파라미터로만 받고, 그게 진짜 본인인지
  확인하는 절차 자체가 없었음.
- **예상 피해**: 아무나 임의의 playerId를 쿼리에 넣어서 남의 채팅 세션에 접속 가능.
- **해결 방법**: 핸드셰이크 시점에 `playerId`+`token`을 같이 받아서
  `TokenVerification.IsValid`(읽기 전용, 회전 없음)로 검증. 채팅 서버는 회전 권한이
  없다는 원칙("서비스 제공 가능 여부는 전적으로 메인 서버한테 맡긴다")도 이때 같이 확정.
- **결과**: 해결됨.

---

## 11. `AccountServer`가 RabbitMQ 발행 중 죽으면 이벤트가 유실될 수 있는 문제 (이중 쓰기 문제)

- **발견**: 친구 삭제 → 채팅 서버 통보 흐름을 설계하며, "계정 정보 갱신은 성공했는데
  RabbitMQ 발행 전에 프로세스가 죽으면?"이라는 질문에서 발견.
- **원인**: 널리 알려진 "이중 쓰기 문제"(dual-write problem) - DB 갱신과 메시지 발행이
  분리된 두 단계라, 하나만 성공하고 다른 하나가 실패하면 시스템이 불일치 상태에 빠짐.
- **예상 피해**: 친구를 삭제했는데 채팅 서버는 영영 모르게 되어, 이미 없는 친구 관계의
  채팅방이 계속 살아있음.
- **해결 방법**: 트랜잭션 아웃박스 패턴 도입. 친구 목록 갱신과 "이 이벤트를 나중에
  발행해야 함" 기록을 같은 자리(순수 인메모리, 원자적)에 같이 남기고, 별도 백그라운드
  서비스(`FriendEventPublisher`)가 그 기록을 계속 훑으면서 실제 RabbitMQ 발행을 시도 -
  확실히 성공해야만(브로커의 발행 확인까지 받아야만) 기록을 지움. 소비 쪽
  (`ChatServer.FriendRemovedSubscriber`)도 수동 ack(받고 처리하고 ack)로 짝을 맞춰서,
  둘이 합쳐 "최소 한 번은 처리된다"를 보장.
- **결과**: 해결됨. 완전한 durability는 아직 아님(계정 데이터 자체가 인메모리 더미라서) -
  나중에 진짜 DB로 옮기면 이 아웃박스도 DB 트랜잭션으로 같이 묶을 것.

---

## 12. `RabbitMqConsumer`를 싱글톤으로 등록했더니 여러 구독이 서로의 채널을 덮어씀

- **발견**: `FriendRemovedSubscriber`에 이어 `SessionInvalidatedSubscriber`를 추가로
  만들면서, 둘 다 같은 `RabbitMqConsumer` 싱글톤 인스턴스를 주입받게 된다는 걸 알아챔.
- **원인**: `RabbitMqConsumer.Subscribe`가 자기 채널(`_channel`)을 **인스턴스 필드**에
  저장하는 구조였음. 같은 인스턴스로 `Subscribe`를 두 번(서로 다른 큐로) 부르면, 두 번째
  호출이 필드를 덮어써서 첫 번째 구독의 콜백이 실행될 때 엉뚱한(두 번째) 채널을
  참조하게 됨.
- **예상 피해**: 두 구독 중 하나(또는 둘 다)가 `BasicAck`/`BasicNack`을 엉뚱한 채널에
  대고 시도해서 예외가 나거나, 메시지 확인 응답이 꼬여서 큐가 이상하게 동작.
- **해결 방법**: `AddMainServerMessaging`에서 `RabbitMqConsumer` 등록을 `AddSingleton`→
  `AddTransient`로 변경(구독마다 반드시 별개 인스턴스). 추가로 `Subscribe` 내부에서도
  인스턴스 필드 대신 클로저가 **지역 변수**를 캡처하도록 고쳐서, 혹시 같은 인스턴스로
  재호출되는 실수가 나더라도 콜백이 자기 채널을 정확히 참조하게 이중 방어.
- **결과**: 해결됨. `MainServer.Common.Messaging/RabbitMqConsumer.cs`,
  `MainServerMessagingExtensions.cs`.

---

## 13. `GlobalWorker`가 지속 업무(공지)를 한 신호씩 밀려서 배달함

- **발견**: S1 통합 테스트("정상 플로우 + 소셜 + 공지") 중, 이미 로비에 있는 클라들한테
  `/admin/announce`로 공지를 쏴도 클라 쪽에 영원히 안 뜸. `LobbyServer` 콘솔엔
  `공지 활성화: ServerShutdownSoon`까지는 정상적으로 찍혔는데, 그 뒤로 아무 데도
  전달이 안 됨.
- **원인**: `GlobalWorker.ExecuteAsync` 루프가 매 반복마다 "지금 `_works`를 배달" →
  "그다음에 `_pendingWorks`에서 새 배치를 꺼내 `_works`에 재할당" 순서로 되어 있었음.
  방금 꺼낸 새 배치는 **이번 반복이 아니라 다음 신호가 왔을 때**에야 배달됨 - 그런데
  다음 신호는 누군가 또 `EnterLobby`를 부르거나 공지를 또 쏴야만 발생함. 아무도 새로
  로비에 안 들어오면 그 배치는 `_works`에 담긴 채로 영원히 배달 안 됨.
- **예상 피해**: 이미 접속 중인 전원한테 공지를 못 보냄(이번 발견 케이스) - 앞으로
  "지금 접속 중인 전원 + 이후 새 접속자"패턴(`GlobalWorker`)을 쓰는 기능이 늘어날수록
  똑같이 걸림.
- **해결 방법**: 두 `if` 블록 순서를 맞바꿔서, "꺼내기" 먼저 하고 그 결과를 "같은
  반복 안에서" 바로 배달하도록 수정.
- **결과**: 해결됨. `LobbyServer/Services/GlobalWorker.cs`.

---

## 14. `IngameFriendManager`가 인게임 친구 명령마다 네이티브 메모리 에러를 냄

- **발견**: 인게임 중 친구 명령(`AddFriend` 등)을 보낼 때마다 콘솔에
  `Invalid allocation label passed to UnsafeUtility::Free`가 찍힘. 게임 로직 자체는
  멀쩡히 동작해서 한동안 무해한 경고로 오인했음.
- **원인**: `SendFriendCommandAsync`가 `using var writer = new FastBufferWriter(...,
  Allocator.Temp)`로 만든 writer를 `SendNamedMessage` 직후가 아니라
  `return await _pendingFriendCommand.Task;` 뒤까지 들고 있었음(async 메서드에서
  `using var`는 메서드 전체 스코프가 끝나야 Dispose됨). `Allocator.Temp`는 그 프레임
  안에서만 유효한데, 서버 응답이 올 때까지(최소 한 프레임 이상) writer가 살아있다가
  뒤늦게 Dispose되면서 이미 끝난 스코프의 할당을 해제하려다 터짐.
- **예상 피해**: 네이티브 메모리 에러가 매 호출마다 누적 - 당장 크래시는 안 나지만
  장시간 플레이 시 메모리 관리가 흔들릴 위험이 있는 잠재적 불안정 요인.
- **해결 방법**: writer 스코프를 `SendNamedMessage` 호출 직후로 명시적으로 좁혀서
  `await` 전에 확실히 Dispose되게 수정. (같은 패턴을 쓰는 `IngameServerInstance.cs`
  4곳, `ClientInstance.cs` 1곳은 전부 `await` 없이 동기적으로 끝나서 문제없음을 같이
  확인함 - 이 파일만 유일하게 예외였음.)
- **결과**: 해결됨. `Network/Client/IngameFriendManager.cs`.

---

## 15. 매칭 서버가 죽으면 대기 중이던 클라가 영원히 응답을 못 받음

- **발견**: S2 통합 테스트("장애 주입 + 재접속 콤보") 2.4 항목 - 매칭 요청을 보내고
  대기 중인 상태에서 매칭 서버를 강제 종료하면, 클라 콘솔에 `매칭메이킹 요청 완료`
  이후 아무 반응이 없음(성공도 실패도 아닌 무한 대기).
- **원인**: `MatchNotificationDispatcher`는 매칭 서버로부터 실제 알림
  (`MatchNotification`)이 도착했을 때만 반응하는 구조였고, 매칭 서버와의 상시 연결
  (`MatchServerConnection`) 자체가 끊기는 것에 대한 핸들러가 아예 없었음.
  `LobbyControlService.RequestMatch`는 `await Task.Delay(Timeout.Infinite, cts.Token)`로
  대기하는데, 이 `cts`를 취소시켜줄 유일한 경로가 알림 도착뿐이라 매칭 서버가 죽으면
  영원히 안 풀림.
- **예상 피해**: 매칭 서버가 죽거나 재시작될 때마다, 그 순간 대기 중이던 클라 전원이
  응답 없이 멈춤 - 재로그인 전까지 복구 불가.
- **해결 방법**: `IMatchNotificationHandler`에 `HandleConnectionLost()` 추가.
  `MatchServerConnection`이 연결 끊김을 감지하는 두 경로(예외로 끝남 / 조용히 EOF로
  끝남) 둘 다에서 이걸 호출하도록 수정(단, 이 서버 자신의 정상 종료는 제외).
  `MatchNotificationDispatcher.HandleConnectionLost()`는 대기 중이던 전원에게 세션을
  `Lobby`로 되돌리고 `MatchFailed{Reason="매칭 서버 연결 끊김"}`을 통보한 뒤 정리한다.
- **결과**: 해결됨. `Shared/MainServer.Common/MatchConnection/IMatchNotificationHandler.cs`,
  `MatchServerConnection.cs`, `LobbyServer/Services/MatchNotificationDispatcher.cs`.

---

## 16. `ClientInstance`가 `MatchFailed`를 아예 구독하지 않아서 매칭 실패가 조용히 사라짐

- **발견**: 15번을 고친 뒤 재테스트했는데도 클라 콘솔에 여전히 아무것도 안 뜸. 로비
  서버 로그로 서버 쪽은 정상적으로 통보를 시도했다는 게 확인됐는데도 그랬음.
- **원인**: `GrpcLobbyChannel`/`LobbyClient`까지는 `OnMatchFailed` 이벤트가 정상적으로
  배선돼 있었지만, `ClientInstance.InitializeLobbyClient()`가 `OnMatchFound`/
  `OnEnterLobbySuccess`/`OnConflictingSession`은 구독하면서 `OnMatchFailed`만 빠뜨림.
  이벤트는 정확히 발생했지만 아무도 안 듣고 있어서 로그 한 줄 없이 사라짐.
- **예상 피해**: 매칭 실패(매칭 서버 다운뿐 아니라 향후 다른 사유로 인한 실패도 전부
  포함)가 클라에 어떤 형태로도 전달 안 됨 - 사용자는 매칭이 그냥 멈춘 것처럼 보임.
- **해결 방법**: `InitializeLobbyClient()`에 `LobbyClient.OnMatchFailed += OnMatchFailed;`
  추가, 최소한 로그로는 보이는 핸들러 구현(재시도 UI 등 실제 사용자 대응은 TODO).
- **결과**: 해결됨. `Network/Client/ClientInstance.cs`.

---

## 17. Redis가 재시작되면 서버가 영영 "등록 안 된 서버"로 남음

- **발견**: "브로커 다운 상태에서 운영" 테스트 - Redis를 내렸다 올린 뒤, 이미 떠있던
  서버들 콘솔에 `서버 하트비트 갱신 실패 - 등록되지 않은 서버(server:...)`가 계속
  반복해서 찍힘. Redis가 죽어있는 동안은 문제없었는데(하트비트 실패만 로그로 남기고
  계속 재시도), 복구된 뒤에도 계속 실패함.
- **원인**: `RegisterServerAsync`는 서버 기동 시 딱 한 번만 호출되고, 이후
  `RefreshServerHeartbeatAsync`는 `KeyExpireAsync`(기존 키의 TTL만 갱신)만 시도함.
  Redis가 재시작되면서 데이터가 날아가 `server:{id}` 키 자체가 사라졌는데, 갱신
  로직은 "키가 없으면 다시 만든다"는 자가 복구 경로가 없어서 그 서버는 프로세스가
  멀쩡히 살아있어도 영영 등록 안 된 상태로 남음.
- **예상 피해**: `IsServerAliveAsync`로 생존을 확인하는 모든 로직(예: 매칭 서버가
  수용 가능한 인게임 서버를 찾는 `FindAvailableIngameServerAsync`)이 이 서버를
  계속 "죽었다"고 오판 - Redis에 순간적인 장애(재시작 등)만 있었어도 그 이후로
  해당 서버가 매칭 후보에서 영구적으로 배제됨(재시작 전까지).
- **해결 방법**: `RefreshServerHeartbeatAsync`가 address 인자를 받도록 시그니처
  변경. `KeyExpireAsync`가 실패(키 없음)하면 그 자리에서 `StringSetAsync`로 즉시
  재등록하도록 수정 - 갱신 실패를 "당장 고칠 수 있는 상황"으로 바꿈.
- **결과**: 해결됨. `Shared.Common/Redis/ISessionRegistry.cs`,
  `RedisSessionRegistry.cs`, `SessionRegistryConnector.cs`,
  `MainServer.Common/Heartbeat/ServerHeartbeatService.cs`.

---

## 18. Kafka 브로커가 죽어있으면 발행 요청이 5분 동안 안 끝남

- **발견**: "브로커 다운 상태에서 운영" 테스트 - Kafka를 내린 상태에서
  `LobbyServer`의 `/test/kafka`를 호출했더니 서버가 죽지는 않았지만 요청이 몇 분이
  지나도 응답을 안 함(사실상 멈춘 것처럼 보임).
- **원인**: `KafkaProducer`의 `ProducerConfig`에 `MessageTimeoutMs`를 명시적으로
  설정 안 해서 Confluent.Kafka 기본값(5분)을 그대로 씀. 브로커가 아예 응답이 없는
  상황에서는 그 5분을 다 채우고 나서야 실패로 확정됨.
- **예상 피해**: 크래시는 아니지만, Kafka 장애 시 그 발행 경로를 쓰는 요청들이
  최대 5분씩 스레드/커넥션을 붙잡고 있게 됨 - 운영 환경에서는 이게 누적되면
  사실상 서비스 마비와 다를 바 없음.
- **해결 방법**: `MessageTimeoutMs = 10000`(10초)을 명시적으로 설정해서 상한선을
  현실적인 값으로 낮춤.
- **결과**: 해결됨. `MainServer.Common/Messaging/KafkaProducer.cs`.

---

## 19. 인게임 접속 시 세션에 재접속 주소 대신 RoomId를 저장함

- **발견**: "인게임 도중 연결 끊김 → 유예 시간 내 재접속" 테스트 중, 재접속 시도가
  아니라 완전히 새로 로그인해서 재진입하는 경로를 테스트하다가, 클라가 안내받은
  주소(`https://localhost:7000`)로 접속을 시도하자마자 `게임 서버 주소 파싱 실패`가 남.
- **원인**: `GameServerHandler.HandlePlayerJoinedAsync`가
  `session.CurrentServer = joined.RoomId;`로 되어 있었음 - 클라가 나중에 재진입할 때
  써야 할 실제 NGO 접속 주소(host:port) 대신 방 ID(GUID)를 저장하고 있었음.
- **예상 피해**: 로그인 서버를 거치는 재진입(`LoginConnectionHandler`)이 이 값을
  그대로 클라에게 안내하므로, 재진입을 시도하는 모든 케이스가 실패함.
- **해결 방법**: `session.CurrentServer = ClientAddress;`(실제 클라 접속 주소)로 수정.
- **결과**: 해결됨. `GameAgent/Services/GameServerHandler.cs`.

---

## 20. 로그인 서버 재진입이 세션 상태를 안 바꿔줘서 게임 에이전트가 거부함

- **발견**: 19번을 고친 뒤에도 재진입이 여전히 실패 - 이번엔 게임 에이전트 콘솔에
  `접속 거부: 사유=잘못된 세션 상태: Ingame`이 찍힘.
- **원인**: `GameServerHandler.HandleApprovalRequestAsync`의 `ApprovalRequest` 검증은
  `session.Status == Moving`인 경우만 승인한다(정상 매칭 흐름과 판단 기준을 하나로
  유지하기 위함). 그런데 로그인 서버의 재진입 로직은 클라를 인게임 서버로 돌려보내기
  전에 세션 상태를 `Moving`으로 바꿔주는 걸 빠뜨려서, 세션이 계속 `Ingame` 상태인 채로
  접속 시도가 들어와 거부당함.
- **예상 피해**: 재진입 자체가 구조적으로 항상 실패 - 19번만 고쳐서는 재진입이 완성이
  안 됨.
- **해결 방법**: `LoginConnectionHandler`가 인게임으로 안내하기 직전에
  `session.Status = PlayerStatus.Moving`으로 갱신 후 저장하도록 수정. 로비 재진입은
  이 검사 자체가 없어서 그대로 둠.
- **결과**: 해결됨. `LoginServer/Services/LoginConnectionHandler.cs`.

---

## 21. NGO 재접속 시 StartClient()가 "이미 실행 중"이라고 거부함

- **발견**: 재접속 테스트 중 클라 콘솔에 `[Netcode] Cannot start Client while an
  instance is already running`가 반복적으로 뜸. 서버 쪽에서 클라를 강제로 끊는
  테스트 트리거와 맞물려 접속-거부-재시도가 무한 반복되는 상황까지 발생함.
- **원인**: NGO의 `OnClientDisconnectCallback`이 호출되는 시점엔 `NetworkManager`
  내부 종료 절차가 아직 시작조차 안 됐을 수 있음. 처음엔 `ShutdownInProgress`
  플래그만 폴링하는 방식으로 고쳤는데, 그 플래그 자체가 아직 `false`인 채로 대기를
  그냥 통과해버려서 근본적으로 해결이 안 됐음.
- **예상 피해**: NGO 접속이 예기치 않게 끊긴 뒤의 빠른 재접속 경로, 그리고 로그인
  서버를 거치는 재진입 경로 둘 다 구조적으로 실패할 수 있음.
- **해결 방법**: 인게임 접속 진입점 3곳(매치 확정/로그인 재진입/빠른 재접속)을
  `ConnectToIngameServerAsync` 하나로 통합하고, 그 안에서 `IsListening` +
  `ShutdownInProgress`를 같이 확인하면서 실제 시간 간격(100ms, 최대 2초)을 두고
  재시도하는 `WaitForNetworkManagerShutdownAsync`로 교체. 외부에서 취소할 수 있게
  `CancellationToken`도 받게 함(`_cts.Token`과 묶임).
- **결과**: 해결됨(추가 실전 재확인 필요). `Network/Client/ClientInstance.cs`.

---

## 22. 게임 에이전트가 재접 가능한 방에서 세션 상태를 안 바꿔줘서 자기 검증에 걸림

- **발견**: 21번까지 고친 뒤에도 빠른 재접속 경로(로그인 서버를 안 거치는 쪽)만
  여전히 실패 - 게임 에이전트 콘솔에 `접속 거부: 사유=잘못된 세션 상태: Ingame`
  반복. 20번과 증상은 같지만 경로가 달랐음(20번은 로그인 서버 경유 재진입, 이건
  빠른 재접속).
- **원인**: `HandlePlayerLeftAsync`의 "재접 가능한 방" 분기 주석에 "session은 안
  건드림"이라고 명시돼 있었음 - 클라 이탈 시 `Status`를 그대로 `Ingame`에 둔 채
  유예 타이머만 돌림. 그런데 `HandleApprovalRequestAsync`는 `Status==Moving`만
  승인하므로, 게임 에이전트 자기 자신이 만든 두 코드가 서로 모순돼서 재접속
  시도 자체가 항상 거부당함 - 로그인 서버를 거치지 않는 경로라 20번 수정으로는
  못 잡았음.
- **예상 피해**: 빠른 재접속 경로가 사실상 한 번도 성공할 수 없는 구조적 결함 -
  네트워크가 잠깐 끊겼다 붙는 흔한 상황에서 항상 실패.
- **해결 방법**: `HandlePlayerLeftAsync`의 재접 가능한 방 분기에서
  `session.Status = PlayerStatus.Moving`으로 갱신 후 저장하도록 추가. 재접속 성공 시
  `HandlePlayerJoinedAsync`가 다시 `Ingame`으로 되돌려놓아 정상 순환됨.
- **결과**: 해결됨. `GameAgent/Services/GameServerHandler.cs`.

---

## 23. 인게임 접속 콜백이 재접속마다 중복 구독됨

- **발견**: 22번까지 고쳐서 재접속 자체는 성공했는데, 콘솔에 `NGO 접속 성공`이
  한 번의 접속에 두 번씩 찍힘.
- **원인**: `ConnectToIngameServerAsync`가 접속 시도마다(최초 접속/빠른 재접속/
  재진입) 매번 호출되는데, 그 안에서 `networkManager.OnClientConnectedCallback
  += OnNgoClientConnected;`를 구독 해제 없이 `+=`만 하고 있었음. 같은
  `NetworkManager` 인스턴스에 매번 쌓이기만 해서, 두 번째 접속부턴 콜백이
  중복 호출됨(세 번째면 3번, 계속 누적).
- **예상 피해**: 재접속을 반복할수록 `SendPlayerJoinedAsync` 등 접속 시점에
  실행되는 로직 전부가 중복 실행 - 당장은 로그만 중복이지만, 나중에 접속 시점에
  부수 효과가 있는 로직이 추가되면 그대로 중복 실행되는 잠재적 위험.
- **해결 방법**: 구독 전에 `-=`로 먼저 확실히 해제한 뒤 `+=`로 다시 구독(최초
  호출이라 구독된 적이 없어도 `-=`는 안전하게 무시됨).
- **결과**: 해결됨. `Network/Client/ClientInstance.cs`.

---

## 24. 채팅 서버가 남의 WebSocket을 외부에서 직접 닫으려다 영원히 멈춤

- **발견**: "로비 연결 끊김 → 세션 삭제 + 채팅 서버 연결도 같이 끊기는지" 테스트 중,
  로비 세션은 정상 삭제됐지만 채팅 연결은 안 끊김. 강제 종료 성공/실패 로그가
  둘 다 안 찍힌 채 그냥 멈춰있었음.
- **원인**: `SessionInvalidatedSubscriber`(RabbitMQ 구독 콜백 - 그 연결과 무관한
  별도 실행 컨텍스트)가 전역 `ChatConnectionRegistry`에서 연결을 찾아
  `WebSocket.CloseAsync()`를 직접 호출했음. `CloseAsync`는 상대방의 종료 응답을
  기다리느라 내부적으로 자기가 직접 `ReceiveAsync`를 한 번 더 호출하는데, 그
  WebSocket은 이미 `WebSocketHandler.ReceiveLoopAsync`(그 연결을 소유한 별도
  태스크)가 자기 몫의 `ReceiveAsync`로 대기 중이었음 - 동시에 두 개의 읽기가
  충돌하면서 영원히 안 끝남.
- **예상 피해**: 세션이 삭제된 뒤에도 채팅 연결이 좀비 상태로 남아 계속 서비스가
  됨 - "메인 서버가 세션을 지우면 채팅 서버도 즉시 따라 끊는다"는 원칙이 실제로는
  전혀 지켜지지 않고 있었음.
- **해결 방법**: 근본 원인은 "연결을 소유하지 않은 외부 태스크가 그 WebSocket을
  직접 조작"한 것이었으므로, 단순히 `CloseOutputAsync`로 바꾸는 패치가 아니라
  소유권 모델 자체를 정리함 - `ChatConnection`에 자기 소유 `CancellationTokenSource`
  추가, `ReceiveLoopAsync`가 이 토큰으로 `ReceiveAsync`를 걺. 외부(구독자)는 이제
  WebSocket을 전혀 안 건드리고 이 토큰만 취소하며, 실제 종료(`CloseOutputAsync`)는
  그 연결을 소유한 `ReceiveLoopAsync` 자신이 취소를 감지한 뒤 수행. "이 WebSocket의
  유일한 조작자는 소유 태스크뿐"이라는 원칙을 지키도록 재설계.
- **결과**: 해결됨. `ChatServer/ChatConnectionRegistry.cs`, `WebSocketHandler.cs`,
  `SessionInvalidatedSubscriber.cs`.

---

## 25. 인게임 접속 시도에 타임아웃이 없어서 성공도 실패도 없이 영원히 멈출 수 있음

- **발견**: "로그인 서버 재진입 - 도착지 서버 사망/토큰 만료 상태에서 재로그인"(이른바
  "대참사" 시나리오, `ARCHITECTURE.md` 참고) 테스트 중 - 게임 에이전트를 죽인 직후
  세션 레지스트리 TTL이 아직 안 지난 상태에서 재진입을 시도했더니, 로그인 서버는
  (죽은) 게임 서버 주소로 정확히 안내했지만, 클라가 그 주소로 NGO 접속을 시도한
  뒤 `NGO 접속 성공`도 `NGO 접속 끊김`도 전혀 안 뜨고 그대로 멈춤.
- **원인**: 대상 서버가 아예 응답이 없는 경우(주소에 아무도 없음, 또는 게임 서버는
  살아있어도 `ConnectionApproval`이 게임 에이전트에 검증을 요청한 뒤 그 게임
  에이전트가 죽어서 응답을 영원히 못 받는 경우), NGO가 `OnClientConnectedCallback`도
  `OnClientDisconnectCallback`도 아예 안 불러줌. 접속 시도 자체에 타임아웃이 없어서
  이 상태를 감지할 방법이 없었음.
- **예상 피해**: `ARCHITECTURE.md`가 명시한 설계 의도("자동 재시도 없이 조용히
  실패하고 사람이 재시도")가 "조용히 실패"가 아니라 "실패 신호조차 없이 무한정
  멈춤"이 되어버림 - 나중에 "연결이 끊겼습니다" UI를 붙이려 해도 붙일 시점 자체가
  없음. 서버 쪽도 대칭적으로 `ConnectionApproval`을 `Pending` 상태로 영원히 들고
  있어 좀비 상태가 됨.
- **해결 방법**: 클라-서버 공용 타임아웃 상수
  (`AutoBattler.Network.NetworkTimeouts.ConnectApprovalTimeout`, 10초) 신설.
  클라는 `ConnectToIngameServerAsync`에서 접속 시도마다 이 시간 안에 성공/실패
  콜백이 없으면 `NetworkManager.Shutdown()`으로 포기. 서버도 `ConnectionApproval`을
  `Pending`으로 돌린 직후 같은 시간을 기다렸다가, 응답이 없으면 거부 처리해서
  좀비 상태를 방지. **포기한 뒤 무엇을 할지(로그인 서버 재진입/UI 안내/프로그램
  종료)는 프로젝트 취향에 따라 갈리는 결정이라 TODO로 남겨두고 구현은 안 함.**
- **결과**: 해결됨(접속 시도 중단까지). `Network/NetworkTimeouts.cs`(신규),
  `Network/Client/ClientInstance.cs`, `Network/Server/IngameServerInstance.cs`.

---

## 26. 전입 티켓 검증이 양방향 모두 사실상 죽은 코드였음

- **발견**: "훔친 영구 토큰으로 목적지 서버에 먼저 도착 시도 - 전입 티켓 없이는
  거부되는지" 테스트 중 - 전입 티켓을 일부러 위조해서 접속을 시도했는데 그대로
  접속이 성공해버림(거부 로그가 전혀 안 뜸).
- **원인**: 게임 에이전트의 `ConnectionApproval` 검증은 `session.OwnerServerType ==
  Lobby`일 때만 티켓을 검사하는데, 매치가 확정되는 순간(`MatchNotificationDispatcher`)
  로비가 클라 접속 전에 `OwnerServerType`을 이미 `Ingame`으로 미리 바꿔놔서, 실제
  접속 시점엔 이 조건이 항상 거짓이 되어 검증 자체가 스킵되고 있었음. 정상 매칭도
  예외 없이 마찬가지였음 - 반대 방향(`LobbyControlService.EnterLobby`의
  `existing.OwnerServerType == Ingame` 조건)도 게임 에이전트가 리다이렉트 준비 시점에
  `OwnerServerType`을 미리 `Lobby`로 바꿔서 똑같이 죽어있었음.
- **예상 피해**: `KNOWN_ISSUES.md` #3에서 만들었던 "훔친 영구 토큰으로 먼저 도착"
  방어가 양방향 모두 실질적으로 존재하지 않는 상태였음 - 토큰만 훔치면(또는
  추측하면) 전입 티켓 없이도 목적지 서버에 진짜 클라보다 먼저 도착할 수 있었음.
- **해결 방법**: `OwnerServerType`으로 "검증이 필요한 상황인지"를 간접 판단하던
  방식을 버리고, `MainServerIntermediary`에 `HasPendingTicket(playerId)`(그 플레이어
  앞으로 등록된 티켓이 있는지 부작용 없이 확인)를 추가해서 직접 판단하도록 양쪽 다
  수정. 티켓이 등록돼 있으면(정상 전환) 반드시 통과해야 하고, 없으면(재접속) 검증
  자체를 스킵 - `OwnerServerType`이 언제 바뀌는지와 완전히 무관해짐.
- **결과**: 해결됨. `Shared.Common/Account/MainServerIntermediary.cs`,
  `GameAgent/Services/GameServerHandler.cs`, `LobbyServer/Services/LobbyControlService.cs`.

---

## 27. 로그인 재진입 시 인게임 주소로 실패한 적 있는 예전 주소를 안내함

- **발견**: 26번을 고쳐서 티켓 거부가 정상적으로 뜨게 된 뒤, 그 직후 자동으로 이어진
  로그인 서버 재진입에서 클라가 `localhost:5200`(로비 주소)으로 NGO 접속을
  시도하다가 10초 타임아웃으로 포기함 - 인게임 주소를 물어봤는데 로비 주소가 온 것.
- **원인**: 재진입 시 인게임 목적지 주소로 `session.CurrentServer`를 썼는데, 이
  필드는 실제 접속이 성공했을 때만(`HandlePlayerJoinedAsync`) 갱신됨. 이번 케이스처럼
  접속이 (티켓 거부로) 한 번도 성공 못 한 채 재진입하면, `CurrentServer`엔 그 이전
  단계(로비)의 값이 그대로 남아있어서 그걸 그대로 안내해버림.
- **예상 피해**: 접속 실패(전입 티켓 거부뿐 아니라 어떤 이유로든 최초 접속이
  한 번도 안 끝난 상태) 직후의 재진입이 전부 엉뚱한 주소로 안내됨.
- **해결 방법**: `session.NextServer`(매치 확정 시점에 실제 NGO 주소로 채워지고,
  접속 성공 시에만 비워짐)를 우선 사용하고, 비어있을 때만 `CurrentServer`로
  폴백하도록 수정 - "아직 한 번도 못 도착함"과 "이전에 도착했었음"을 정확히 구분.
- **결과**: 해결됨. `LoginServer/Services/LoginConnectionHandler.cs`.

---

## 28. `PlayerStatus.Matching`이 실질적으로 죽은 상태였고, 오히려 세션 리크를 만들고 있었음

- **발견**: 26/27번을 고치는 대화 중, "매칭서버는 클라를 받는 곳이 아닌데 왜 별도
  상태가 있나"는 의문에서 코드 전체를 점검하다가 발견.
- **원인**: `PlayerStatus.Matching`은 `LobbyControlService.RequestMatch`에서 설정만
  될 뿐, 코드베이스 어디에서도 이 값을 조건으로 검사하는 곳이 없었음. 오히려
  `HandleStayConnectedEndedAsync`(로비 연결 끊김 시 세션 정리)의 조건이
  `Status==Lobby`일 때만 정리하도록 되어 있어서, 매칭 대기 중(`Status==Matching`)
  끊긴 세션은 정리 대상에서 빠짐. `RequestMatch`의 자체 연결 끊김 처리도 매칭
  서버에서 빼달라고 통지만 할 뿐 세션 자체는 안 건드림 - 결과적으로 매칭 대기
  중 끊기면 세션이 양쪽 어디서도 안 지워지고 영영 남는 리크였음.
- **예상 피해**: 매칭 요청 중 연결이 끊기는 흔한 상황마다 세션 레지스트리에
  고아 세션이 계속 쌓임.
- **해결 방법**: `Matching` 상태 자체를 제거 - 매칭 대기 중에도 `Status`는 계속
  `Lobby`로 유지. 그러면 `HandleStayConnectedEndedAsync`의 기존 정리 조건이 이
  케이스도 자동으로 커버함.
- **결과**: 해결됨. `Shared.Common/Redis/ISessionRegistry.cs`(enum),
  `LobbyServer/Services/LobbyControlService.cs`.

---

## 29. 채팅 클라가 서버 Error 응답을 받으면 대기 중인 요청이 영원히 안 풀림

- **발견**: "친구 삭제 직후 메시지" 보안 회귀 테스트 중 - 친구 삭제로 방이 사라진
  채널에 `GetHistoryAsync`를 불렀더니, 서버가 `NOT_A_MEMBER` `Error`로 정상
  응답했는데도 호출부가 그대로 멈춰서 테스트 로그가 중간에 끊김(최종 판정 줄이
  아예 안 찍힘).
- **원인**: `WebSocketChatChannel`의 `RequestChatAsync`/`GetMyRoomsAsync`/
  `GetHistoryAsync`는 서버 응답을 `TaskCompletionSource`로 기다리는데, 수신 루프의
  `Error` 케이스는 로그만 남기고 그 어떤 pending TCS도 풀어주지 않았음 - 서버가
  정확히 그 요청에 대한 에러로 응답해도 호출자는 영원히 기다림.
- **예상 피해**: 서버가 명시적으로 거부 응답을 준 정상적인 상황(권한 없음 등)에서도
  클라가 멈춰버림 - 나중에 UI가 붙으면 그 화면 자체가 먹통이 됨.
- **해결 방법**: `Error` 케이스에서 그 시점에 대기 중인 세 TCS(`_pendingRequestChat`/
  `_pendingGetMyRooms`/`_pendingGetHistory`)를 전부 예외로 풀어주도록 수정 - 이 셋은
  설계상 "한 번에 하나씩만" 진행되므로 실제로는 최대 하나만 대기 중이다.
- **결과**: 해결됨. `Network/Client/WebSocketChatChannel.cs`.

---

## 30. Kestrel에 HTTP/2 keepalive가 없어서 클라 연결 끊김 감지 시간이 예측 불가능했음

- **발견**: "매칭 대기 중 클라 연결 끊김" 테스트 중 - 서버가 아무것도 안 보내는
  스트림(`StayConnected`, `RequestMatch`처럼 `Task.Delay(Timeout.Infinite,
  context.CancellationToken)`으로 그냥 대기만 하는 것들)은 연결이 끊겨도 그
  사실을 알아챌 방법이 전적으로 OS 수준 TCP 타임아웃에 의존하고 있었음 - 어떤
  때는 61초, 어떤 때는 1분을 훌쩍 넘겨도 감지가 안 됨.
- **원인**: `KestrelEndpointExtensions.ConfigureGameServerEndpoints`(모든 서버가
  공용으로 쓰는 Kestrel 설정)에 HTTP/2 PING 기반 keepalive가 전혀 설정 안 돼있었음.
- **예상 피해**: "클라가 끊기면 서버가 정리한다"에 의존하는 모든 로직(세션 정리,
  매칭 이탈 통보 등)이 실제로는 몇 분씩 지연될 수 있었음 - 지금까지 이 문서의
  다른 항목(#4, #24 등)에서 "연결 종료까지 61초 걸림" 같은 관찰이 반복됐던
  근본 원인이기도 함.
- **해결 방법**: `KeepAlivePingDelay=15초`, `KeepAlivePingTimeout=15초`를 명시적으로
  설정 - 최대 30초 안에는 확실하게 끊긴 연결로 판정되게 함. 모든 서버가 공유하는
  확장 메서드 한 곳만 고쳐서 전체 서버에 일괄 적용됨.
- **테스트 방법론 메모**: 이 확인 과정에서, MPPM 뷰포트를 X 버튼으로 닫는 것과
  Play 버튼으로 테스트 자체를 정지하는 것이 다르다는 것도 확인됨 - 뷰포트만
  닫으면 그 밑의 프로세스/연결이 실제로는 안 죽고 계속 살아있는 것으로 보임.
  "클라 연결이 실제로 끊기는" 상황을 재현하려면 Play를 정지해야 확실함.
- **결과**: 해결됨. `Shared.Common/Hosting/KestrelEndpointExtensions.cs`.

---

## 31. 매칭 이탈 통보가 하드코딩된 가정에 의존했음 - `ConnectedClientInfo`로 개선

- **발견**: 위 30번을 조사하는 과정에서, `HandleStayConnectedEndedAsync`(클라 연결
  끊김 감지)가 매칭 서버에 이탈을 알려야 하는지 판단할 근거가 없어서 처음엔
  "일단 무조건 알린다"는 식으로 임시 처리했었음 - 무해하지만 부정확함.
- **개선**: 로비 연결(`LobbyConnectionRegistry`)이 관리하는 클라 정보에
  `ConnectedClientInfo`(요청한 작업과 그 진행 상태를 담는 `Tasks` 딕셔너리)를
  신설. `RequestMatch`가 대기 시작 시 `Tasks[Matching]=Pending`을 남기고 스트림이
  끝날 때 지우며, `HandleStayConnectedEndedAsync`는 이제 이 상태를 실제로 확인해서
  `Pending`일 때만 매칭 서버에 이탈을 통보함 - 하드코딩된 가정 대신 실제 상태
  기반 판단으로 교체.
- **결과**: 개선됨. `Shared.Common`이 아니라 `LobbyServer/Services`에 신설:
  `ConnectedClientInfo.cs`(신규), `LobbyConnectionRegistry.cs`,
  `LobbyControlService.cs`.
