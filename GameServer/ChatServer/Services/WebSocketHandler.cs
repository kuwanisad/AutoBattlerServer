using ChatContracts;
using System.Net.WebSockets;
using Google.Protobuf;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

/// <summary>
/// 클라이언트 WebSocket 연결을 처리한다.
/// Protobuf 바이너리로 메시지를 송수신한다.
/// </summary>
public sealed class WebSocketHandler
{
    private readonly ChatChannelManager _channelManager;
    private readonly IChatMessageStore _messageStore;
    private readonly IChatFilter _chatFilter;
    private readonly ILogger<WebSocketHandler> _logger;

    public WebSocketHandler(
        ChatChannelManager channelManager,
        IChatMessageStore messageStore,
        IChatFilter chatFilter,
        ILogger<WebSocketHandler> logger)
    {
        _channelManager = channelManager;
        _messageStore = messageStore;
        _chatFilter = chatFilter;
        _logger = logger;
    }

    public async Task HandleAsync(HttpContext context)
    {
        using var webSocket = await context.WebSockets.AcceptWebSocketAsync();

        // TODO: JWT 토큰 검증으로 교체
        var playerId = context.Request.Query["playerId"].ToString();
        if (string.IsNullOrEmpty(playerId))
        {
            await webSocket.CloseAsync(WebSocketCloseStatus.PolicyViolation,
                "playerId 필요", CancellationToken.None);
            return;
        }

        var connection = new ChatConnection
        {
            PlayerId = playerId,
            WebSocket = webSocket,
        };

        await _channelManager.OnConnectedAsync(connection);

        try
        {
            await ReceiveLoopAsync(connection);
        }
        finally
        {
            _channelManager.OnDisconnected(playerId);
        }
    }

    private async Task ReceiveLoopAsync(ChatConnection connection)
    {
        var buffer = new byte[4096];

        try
        {
            while (connection.WebSocket.State == WebSocketState.Open)
            {
                var result = await connection.WebSocket.ReceiveAsync(
                    new ArraySegment<byte>(buffer), CancellationToken.None);

                if (result.MessageType == WebSocketMessageType.Close)
                {
                    await connection.WebSocket.CloseAsync(
                        WebSocketCloseStatus.NormalClosure, "정상 종료", CancellationToken.None);
                    break;
                }

                if (result.MessageType != WebSocketMessageType.Binary)
                {
                    continue;
                }

                var message = ClientChatMessage.Parser.ParseFrom(buffer, 0, result.Count);
                await HandleMessageAsync(connection, message);
            }
        }
        catch (WebSocketException ex)
        {
            _logger.LogDebug("WebSocket 연결 종료: {PlayerId} - {Message}",
                connection.PlayerId, ex.Message);
        }
    }

    private async Task HandleMessageAsync(ChatConnection connection, ClientChatMessage message)
    {
        switch (message.PayloadCase)
        {
            case ClientChatMessage.PayloadOneofCase.Join:
                _channelManager.JoinChannel(connection.PlayerId, message.Join.ChannelId);
                await connection.SendAsync(new ServerChatMessage
                {
                    JoinAck = new JoinChannelAck { ChannelId = message.Join.ChannelId, Success = true },
                });
                break;

            case ClientChatMessage.PayloadOneofCase.Leave:
                _channelManager.LeaveChannel(connection.PlayerId, message.Leave.ChannelId);
                await connection.SendAsync(new ServerChatMessage
                {
                    LeaveAck = new LeaveChannelAck { ChannelId = message.Leave.ChannelId, Success = true },
                });
                break;

            case ClientChatMessage.PayloadOneofCase.Send:
                await HandleChatMessageAsync(connection, message.Send);
                break;

            default:
                _logger.LogWarning("알 수 없는 메시지 타입: {PlayerId}", connection.PlayerId);
                break;
        }
    }

    private async Task HandleChatMessageAsync(ChatConnection connection, SendMessageRequest request)
    {
        var filterResult = await _chatFilter.FilterAsync(connection.PlayerId, request.Content);

        if (!filterResult.Allowed)
        {
            await connection.SendAsync(new ServerChatMessage
            {
                Error = new ChatError { Code = "BLOCKED", Message = filterResult.Reason ?? "차단된 메시지" },
            });
            return;
        }

        var message = new ChatMessageData
        {
            MessageId = Guid.NewGuid().ToString("N"),
            ChannelId = request.ChannelId,
            SenderId = connection.PlayerId,
            Content = filterResult.FilteredContent ?? request.Content,
            TimestampUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        };

        await _messageStore.SaveAsync(message);
        await _channelManager.BroadcastAsync(request.ChannelId, message);

        // TODO: Kafka에 채팅 로그 발행
    }
}
