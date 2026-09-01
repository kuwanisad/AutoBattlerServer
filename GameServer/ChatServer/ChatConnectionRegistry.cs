using System.Collections.Concurrent;
using System.Net.WebSockets;

namespace ChatServer;

/// <summary>
/// 지금 채팅 서버에 접속 중인 클라 목록. 딱 "누가 지금 붙어있고 그 웹소켓이 뭔지"만
/// 안다 - 방/채널 멤버십 같은 건 여기 없다(그건 IChatRoomStore가 별도로 담당).
///
/// SessionInvalidatedSubscriber가 강제 종료 대상을 찾을 때, WebSocketHandler가 접속/
/// 해제 시 등록/해제할 때 쓴다.
/// </summary>
public sealed class ChatConnectionRegistry
{
    private readonly ConcurrentDictionary<string, ChatConnection> _connections = new();

    public void Register(ChatConnection connection) => _connections[connection.PlayerId] = connection;

    public void Unregister(string playerId) => _connections.TryRemove(playerId, out _);

    public ChatConnection? TryGet(string playerId) =>
        _connections.TryGetValue(playerId, out var connection) ? connection : null;

    public int Count => _connections.Count;
}

/// <summary>채팅 서버에 접속한 클라이언트 연결 하나.</summary>
public sealed class ChatConnection
{
    public string PlayerId { get; init; } = string.Empty;
    public WebSocket WebSocket { get; init; } = null!;
}
