using ChatContracts;
using Google.Protobuf;
using Microsoft.Extensions.Logging;
using Shared.Common.Redis;
using System.Collections.Concurrent;

/// <summary>
/// 채팅 채널 멤버 관리.
/// Redis에 채널 멤버를 기록하고 조회한다.
/// Redis Pub/Sub으로 채팅 서버 인스턴스 간 메시지를 동기화한다.
///
/// 채널 키 구조:
///   channel:{channelId}:members ← 채널에 있는 플레이어 목록 (Redis Set)
/// </summary>
public sealed class ChatChannelManager : IDisposable
{
    private readonly ISessionRegistry _sessionRegistry;
    private readonly ILogger<ChatChannelManager> _logger;

    private readonly ConcurrentDictionary<string, ChatConnection> _connections = new();
    private readonly ConcurrentDictionary<string, HashSet<string>> _localChannelMembers = new();
    private readonly object _channelLock = new();

    public ChatChannelManager(ISessionRegistry sessionRegistry, ILogger<ChatChannelManager> logger)
    {
        _sessionRegistry = sessionRegistry;
        _logger = logger;
    }

    public async Task OnConnectedAsync(ChatConnection connection)
    {
        _connections[connection.PlayerId] = connection;

        var session = await _sessionRegistry.GetAsync(connection.PlayerId);
        if (session == null)
        {
            _logger.LogWarning("세션 없는 플레이어 채팅 접속: {PlayerId}", connection.PlayerId);
            return;
        }

        // TODO: 세션 상태에 따라 허용 채널 결정
        _logger.LogInformation("채팅 서버 접속: {PlayerId} (Status={Status})",
            connection.PlayerId, session.Status);
    }

    public void OnDisconnected(string playerId)
    {
        _connections.TryRemove(playerId, out _);

        lock (_channelLock)
        {
            foreach (var members in _localChannelMembers.Values)
                members.Remove(playerId);
        }

        _logger.LogInformation("채팅 서버 퇴장: {PlayerId}", playerId);
    }

    public void JoinChannel(string playerId, string channelId)
    {
        lock (_channelLock)
        {
            if (!_localChannelMembers.TryGetValue(channelId, out var members))
            {
                members = new HashSet<string>();
                _localChannelMembers[channelId] = members;
            }
            members.Add(playerId);
        }

        _logger.LogDebug("채널 입장: {PlayerId} → {ChannelId}", playerId, channelId);
    }

    public void LeaveChannel(string playerId, string channelId)
    {
        lock (_channelLock)
        {
            if (_localChannelMembers.TryGetValue(channelId, out var members))
                members.Remove(playerId);
        }

        _logger.LogDebug("채널 퇴장: {PlayerId} → {ChannelId}", playerId, channelId);
    }

    /// <summary>채널에 메시지를 브로드캐스트한다.</summary>
    public async Task BroadcastAsync(string channelId, ChatMessageData message)
    {
        HashSet<string> members;

        lock (_channelLock)
        {
            if (!_localChannelMembers.TryGetValue(channelId, out var m))
                return;
            members = new HashSet<string>(m);
        }

        // ChatMessageData → ServerChatMessage로 변환
        var serverMessage = new ServerChatMessage
        {
            Message = new ChatMessageDelivery
            {
                MessageId = message.MessageId,
                ChannelId = message.ChannelId,
                SenderId = message.SenderId,
                Content = message.Content,
                TimestampUnixMs = message.TimestampUnixMs,
            },
        };

        var tasks = new List<Task>();
        foreach (var memberId in members)
        {
            //if (memberId == message.SenderId) continue;
            if (!_connections.TryGetValue(memberId, out var connection)) continue;
            tasks.Add(connection.SendAsync(serverMessage));
        }

        await Task.WhenAll(tasks);
    }

    public int ConnectionCount => _connections.Count;

    public void Dispose() { }
}

/// <summary>채팅 서버에 접속한 클라이언트 연결.</summary>
public sealed class ChatConnection
{
    public string PlayerId { get; set; } = string.Empty;
    public System.Net.WebSockets.WebSocket WebSocket { get; set; } = null!;

    public async Task SendAsync(ServerChatMessage message)
    {
        if (WebSocket.State != System.Net.WebSockets.WebSocketState.Open)
            return;

        var bytes = message.ToByteArray();
        await WebSocket.SendAsync(
            new ArraySegment<byte>(bytes),
            System.Net.WebSockets.WebSocketMessageType.Binary,
            endOfMessage: true,
            CancellationToken.None);
    }
}
