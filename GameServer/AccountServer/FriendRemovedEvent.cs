namespace AccountServer;

/// <summary>친구 삭제 이벤트. 채팅 서버(FriendRemovedSubscriber)가 구독해서 둘만의 채팅방을 정리하는 데 쓴다.
/// 채팅 서버 쪽 같은 이름의 타입과 필드가 같아야 한다(JSON으로 주고받음).</summary>
public sealed class FriendRemovedEvent
{
    public const string QueueName = "friend-removed";

    public string PlayerId { get; set; } = string.Empty;
    public string FriendPlayerId { get; set; } = string.Empty;
}
