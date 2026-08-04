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
  - `session:{playerId}` ← 플레이어 세션 (기존)
  - `room:{roomId}:info` ← 방 상태 (추가 필요)
  - `room:{roomId}:players` ← 방에 있는 플레이어 목록 (추가 필요)
- 재접속 시 로비 서버가 방 상태를 조회해서 게임이 아직 진행 중인지 확인 후 지시한다.

**미결 사항**
- `PlayerSession`에 추가할 필드 확정
  - `RoomId` ← 어느 방에 있는지 (재접속 시 필요)
  - `AllowReconnect` ← 방의 재접속 허용 여부
  - `ReconnectDeadlineUnixMs` ← 재접속 가능 마감 시각
- `LoginResponse`에 재접속 정보(`RedirectInfo`) 추가 여부
- Moving 상태에서 로그인 시 처리 흐름 확정
  - 재접속 가능한 방이고 게임 진행 중: 인게임 서버 주소 + MatchToken 재발급 반환
  - 재접속 불가능한 방 또는 게임 종료: 세션 Lobby로 초기화 + 패널티 적용 여부
- MatchToken 재발급 주체 (누가, 언제 발급하나) ← 2번 문제
  - 롤링 토큰 패턴 사용 (특허 참고: https://patents.justia.com/patent/20160080383)
  - Redis List로 최신 토큰 2개 유지
    - 새 토큰 발급 시 LPUSH + LTRIM으로 이전 토큰 자동 삭제
    - 클라가 새 토큰을 못 받고 죽어도 이전 토큰으로 재접속 가능
    - 클라가 새 토큰을 성공적으로 쓰면 이전 토큰은 자동 만료
  - 구현 시 Redis key: `room:{roomId}:tokens:{playerId}`
- 게임 서버 크래시 시 방 상태 복구 방안 ← 3번 문제
  - 마스터 서버가 하트비트 끊긴 게임 에이전트의 잔업을 처리
  - `InstanceInfo`에 해당 에이전트가 담당하는 방 목록을 기록해두는 구조 필요
    (단순 List<string>이 아닐 수 있으므로 구현 시 재설계)
  - 잔업 처리: Redis의 방 상태 업데이트 + 플레이어 세션 정리
- `ingame.proto`의 `OpenRoom`에 `allow_reconnect` 필드 추가

### Redis 장애 대응

**결정된 것**
- Redis Sentinel 또는 Cluster로 고가용성 확보 (운영 환경)
- 서킷 브레이커 패턴으로 Redis 장애 시 제한적 동작 모드 전환

**미결 사항**
- 서킷 브레이커 구현 방식 및 각 서버별 폴백 동작 확정
  - 로비 서버: 신규 로그인 거부, 기존 연결 유지
  - 게임 에이전트: 진행 중인 게임 유지, 신규 접속 거부
  - 매칭 서버: 매칭 중단
- 개발 환경에서는 단일 Redis로 유지, 운영 환경 전환 시 Sentinel/Cluster 적용

---

## 구현 필요

- 퇴장 후 로비 복귀 검증 테스트
- Redis 방 상태 관리 (`room:{roomId}:info`, `room:{roomId}:players`)
- **채팅 서버 구현**
  - 표층 서버. 로그인~로그아웃 내내 클라와 WebSocket 연결 유지
  - 마스터 서버와 gRPC (chat.proto)
  - Redis: 채널 멤버 관리 (`channel:{channelId}:members`), 레이트 리밋
  - Redis Pub/Sub: 채팅 서버 인스턴스 간 메시지 동기화
  - Kafka: 채팅 로그, 욕설/스팸 이벤트 발행
  - RabbitMQ: 신고 처리 work queue
  - IChatMessageStore: 메시지 저장 (인메모리 우선, 추후 디스크 티어링)
  - 필터링: 더미로 구현 (추후 룰 기반 또는 ML로 교체)
  - 채널 종류: 친구(`friend:{friendshipId}`), 귓말(`whisper:{userA}:{userB}`), 길드(`guild:{guildId}`)
  - ClientInstance에 ChatClient 추가 (LobbyClient/IngameClient와 독립적으로 항상 활성)
  - 인게임 내 채팅(팀/매치)은 NGO가 담당, 채팅 서버와 무관
- 매칭 서버 구현
- **백업 서버** — 마스터 서버를 제외한 다른 서버가 죽었을 때 대신 업무를 이어받는 서버. 설계 및 구현은 추후 논의.
- `ARCHITECTURE.md` 갱신 (각 과제 구현 완료 시)

---

## 구현 완료

- [완료] Redis Session Registry 기본 구조
- [완료] 로비 서버 ↔ Redis 세션 등록/업데이트
- [완료] 게임 에이전트 ↔ Redis 세션 업데이트
- [완료] NGO ConnectionApproval (playerId + matchToken 검증)
- [완료] clientId ↔ playerId 매핑
