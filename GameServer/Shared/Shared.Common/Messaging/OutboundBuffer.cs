namespace Shared.Common.Messaging;

/// <summary>
/// 보낼 것을 쌓아두는 큐. 연결이 없을 때 쌓고, 연결되면 보내는 쪽이 앞에서부터 꺼내 보낸다.
/// 전송 수단과 무관하다 - "무엇을 보내기 성공으로 볼지"는 쓰는 쪽(RabbitMqBroker 등)이 정하고,
/// 성공했을 때만 <see cref="RemoveIfHead"/>로 뺀다(보내다 끊기면 다음 연결에서 같은 것부터 다시).
///
/// 한도(limit):
/// - -1이면 무한. 기본값이다 - 보상 지급처럼 하나라도 잃으면 안 되는 메시지를 위해.
/// - 0 이상이면 넘칠 때 가장 오래된 것부터 버린다 - 채팅처럼 일부 유실이 괜찮고, 오래 쌓인
///   것보다 최신이 중요한 경우를 위해.
///
/// 메모리에만 있으므로 프로세스가 죽으면 쌓인 것은 사라진다(파일 영속화는 일부러 하지 않음).
/// </summary>
public sealed class OutboundBuffer<T> where T : class
{
    private readonly object _lock = new();
    private readonly LinkedList<T> _items = new();

    public OutboundBuffer(int limit = -1)
    {
        if (limit < -1) throw new ArgumentOutOfRangeException(nameof(limit), "한도는 -1(무한) 또는 0 이상");
        Limit = limit;
    }

    public int Limit { get; }

    public int Count
    {
        get { lock (_lock) return _items.Count; }
    }

    /// <summary>뒤에 넣는다. 한도를 넘겨서 버린 개수를 돌려준다(보통 0).</summary>
    public int Enqueue(T item)
    {
        lock (_lock)
        {
            _items.AddLast(item);

            var dropped = 0;
            if (Limit >= 0)
            {
                while (_items.Count > Limit && _items.First != null)
                {
                    _items.RemoveFirst();
                    dropped++;
                }
            }
            return dropped;
        }
    }

    public bool TryPeek(out T? item)
    {
        lock (_lock)
        {
            item = _items.First?.Value;
            return item != null;
        }
    }

    /// <summary>
    /// 맨 앞이 item일 때만 뺀다. 보내는 동안 한도 초과로 그 항목이 이미 버려졌을 수 있어서,
    /// "맨 앞을 무조건 빼기"를 하면 아직 안 보낸 다음 항목을 잃는다.
    /// </summary>
    public bool RemoveIfHead(T item)
    {
        lock (_lock)
        {
            if (_items.First == null || !ReferenceEquals(_items.First.Value, item)) return false;
            _items.RemoveFirst();
            return true;
        }
    }
}
