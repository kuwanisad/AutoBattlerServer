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
