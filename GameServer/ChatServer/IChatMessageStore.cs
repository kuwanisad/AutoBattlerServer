namespace ChatServer;

/// <summary>
/// 채팅 메시지 저장 추상화. Redis 구현체(RedisChatMessageStore)가 기본이다 - 예전엔
/// 인스턴스 로컬 인메모리였는데, 그러면 플레이어가 다른 채팅 서버 인스턴스로 재접속할 때
/// 새 인스턴스가 과거 기록을 모르는 문제가 있었다. 채팅방 저장소(IChatRoomStore)를 위해
/// 어차피 Redis 연결을 새로 만드는 김에 같이 해결한다.
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
