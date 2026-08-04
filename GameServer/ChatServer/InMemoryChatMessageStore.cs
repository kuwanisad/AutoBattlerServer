using System.Collections.Concurrent;

/// <summary>
/// 인메모리 채팅 메시지 저장 구현체.
/// 채널별로 최근 메시지를 메모리에 보관한다.
/// 용량/시간 초과 시 디스크로 내리는 로직은 추후 구현.
/// </summary>
public sealed class InMemoryChatMessageStore : IChatMessageStore
{
    // 채널별 메시지 목록. 최신 메시지가 앞에 오도록 정렬 유지.
    private readonly ConcurrentDictionary<string, LinkedList<ChatMessageData>> _store = new();

    // 채널당 최대 메시지 수. 초과 시 오래된 메시지 제거.
    // TODO: 설정값으로 빼기, 초과분을 디스크로 내리는 로직 구현
    private const int MaxMessagesPerChannel = 500;

    public Task SaveAsync(ChatMessageData message)
    {
        var messages = _store.GetOrAdd(message.ChannelId, _ => new LinkedList<ChatMessageData>());

        lock (messages)
        {
            messages.AddFirst(message);

            if (messages.Count > MaxMessagesPerChannel)
                messages.RemoveLast();
        }

        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<ChatMessageData>> GetRecentAsync(string channelId, int count)
    {
        if (!_store.TryGetValue(channelId, out var messages))
            return Task.FromResult<IReadOnlyList<ChatMessageData>>(Array.Empty<ChatMessageData>());

        lock (messages)
        {
            var result = messages.Take(count).ToList();
            return Task.FromResult<IReadOnlyList<ChatMessageData>>(result);
        }
    }

    public Task<IReadOnlyList<ChatMessageData>> GetBeforeAsync(string channelId, long beforeTimestampMs, int count)
    {
        if (!_store.TryGetValue(channelId, out var messages))
            return Task.FromResult<IReadOnlyList<ChatMessageData>>(Array.Empty<ChatMessageData>());

        lock (messages)
        {
            var result = messages
                .Where(m => m.TimestampUnixMs < beforeTimestampMs)
                .Take(count)
                .ToList();

            return Task.FromResult<IReadOnlyList<ChatMessageData>>(result);
        }
    }
}
