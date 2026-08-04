/// <summary>
/// 채팅 메시지 저장 추상화.
/// 기본 구현은 InMemoryChatMessageStore.
/// 추후 디스크 티어링, DB 저장 등으로 교체 시 이 인터페이스만 유지하면 된다.
/// </summary>
public interface IChatMessageStore
{
    Task SaveAsync(ChatMessageData message);
    Task<IReadOnlyList<ChatMessageData>> GetRecentAsync(string channelId, int count);
    Task<IReadOnlyList<ChatMessageData>> GetBeforeAsync(string channelId, long beforeTimestampMs, int count);
}

/// <summary>채팅 메시지 내부 데이터.</summary>
public sealed class ChatMessageData
{
    public string MessageId { get; set; } = string.Empty;
    public string ChannelId { get; set; } = string.Empty;
    public string SenderId { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;
    public long TimestampUnixMs { get; set; }
}
