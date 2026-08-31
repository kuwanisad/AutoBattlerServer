using System.Collections.Concurrent;

namespace AccountServer;

/// <summary>친구 삭제 이벤트. 채팅 서버가 구독해서 둘만의 채팅방을 정리하는 데 쓴다.</summary>
public sealed class FriendRemovedEvent
{
    public string PlayerId { get; set; } = string.Empty;
    public string FriendPlayerId { get; set; } = string.Empty;
}

/// <summary>
/// 트랜잭션 아웃박스 패턴 - "계정 정보 갱신"과 "이벤트 발행" 사이에 프로세스가 죽으면
/// 채팅 서버가 영영 그 사실을 모르게 되는 문제(이중 쓰기 문제)를 막기 위함.
///
/// RemoveFriend 처리 중, 친구 목록 갱신과 이 큐에 넣는 것을 같은 자리에서(순수 인메모리
/// 연산, 원자적) 같이 하고, 실제로 RabbitMQ에 발행하는 네트워크 I/O는 별도 백그라운드
/// 서비스(FriendEventPublisher)가 맡는다 - 발행이 확실히 성공해야만 큐에서 뺀다.
/// 그 사이 프로세스가 죽는 위험 구간이 "네트워크 I/O 전체"에서 "순수 인메모리 연산
/// 하나"로 줄어든다.
///
/// 지금은 이 큐 자체가 인메모리라(계정 데이터 전체가 아직 그렇듯) 완전한 durability는
/// 없다 - 나중에 진짜 DB로 옮기면 이 아웃박스도 DB 테이블로 옮겨서 계정 갱신과 같은
/// 트랜잭션으로 묶일 수 있다.
/// </summary>
public sealed class FriendEventOutbox
{
    private readonly ConcurrentQueue<FriendRemovedEvent> _pending = new();

    public void Enqueue(FriendRemovedEvent evt) => _pending.Enqueue(evt);

    public bool TryPeek(out FriendRemovedEvent evt) => _pending.TryPeek(out evt!);

    public bool TryDequeue(out FriendRemovedEvent evt) => _pending.TryDequeue(out evt!);
}
