using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Threading;

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

    // 이 WebSocket의 유일한 조작자는 이 연결을 소유한 ReceiveLoopAsync 태스크뿐이어야
    // 한다. 외부(SessionInvalidatedSubscriber 등)가 강제로 끊어야 할 때도 WebSocket을
    // 직접 건드리지 않고 이 토큰만 취소한다 - 실제 종료(CloseOutputAsync 등)는 소유
    // 태스크가 취소를 감지한 뒤 자기 쪽에서 수행한다. (예전엔 외부에서 직접
    // WebSocket.CloseAsync를 불러서, 소유 태스크가 이미 걸어둔 ReceiveAsync와 충돌해
    // 영원히 멈추는 버그가 있었다 - KNOWN_ISSUES.md 참고.)
    public CancellationTokenSource Cts { get; } = new();

    // 외부가 강제 종료를 요청할 때 이유를 여기 남겨두면, 실제 종료를 수행하는 소유
    // 태스크가 그 이유로 클라에게 정상적인 Close 프레임을 보낼 수 있다.
    public string? ForceCloseReason { get; set; }
}