using System.Text.Json;
using MainServer.Common.Messaging;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ChatServer;

/// <summary>친구 삭제 이벤트 - AccountServer.FriendRemovedEvent와 필드가 같아야 한다
/// (JSON 직렬화로 주고받으므로 타입 자체를 공유하진 않는다 - 계정 서버와 채팅 서버가
/// 서로의 내부 타입을 참조할 필요가 없게).</summary>
internal sealed class FriendRemovedEvent
{
    public string PlayerId { get; set; } = string.Empty;
    public string FriendPlayerId { get; set; } = string.Empty;
}

/// <summary>
/// AccountServer가 발행하는 "friend-removed" 큐를 구독해서, 삭제된 두 사람의 둘만의
/// 채팅방을 실제로 정리한다. RabbitMqConsumer의 수동 ack 방식을 그대로 쓴다 - 처리(방
/// 삭제)가 확실히 끝나야만 ack를 보내고, 실패하면 재큐잉돼서 나중에 다시 시도된다.
/// </summary>
public sealed class FriendRemovedSubscriber : IHostedService
{
    private const string FriendRemovedQueueName = "friend-removed";

    private readonly RabbitMqConsumer _consumer;
    private readonly IChatRoomStore _roomStore;
    private readonly ChatConnectionRegistry _connections;
    private readonly ILogger<FriendRemovedSubscriber> _logger;

    public FriendRemovedSubscriber(
        RabbitMqConsumer consumer,
        IChatRoomStore roomStore,
        ChatConnectionRegistry connections,
        ILogger<FriendRemovedSubscriber> logger)
    {
        _consumer = consumer;
        _roomStore = roomStore;
        _connections = connections;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _consumer.Subscribe(FriendRemovedQueueName, HandleAsync);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private async Task<bool> HandleAsync(string json)
    {
        FriendRemovedEvent? evt;
        try
        {
            evt = JsonSerializer.Deserialize<FriendRemovedEvent>(json);
        }
        catch (JsonException ex)
        {
            _logger.LogError(ex, "친구 삭제 이벤트 파싱 실패 - 버림: {Json}", json);
            return true; // 영구적 실패 - 재큐잉해도 계속 실패하므로 ack시키고 버린다
        }

        if (evt == null || string.IsNullOrEmpty(evt.PlayerId) || string.IsNullOrEmpty(evt.FriendPlayerId))
        {
            _logger.LogError("친구 삭제 이벤트 필드 누락 - 버림: {Json}", json);
            return true;
        }

        var channelId = IChatRoomStore.ComputeChannelId(evt.PlayerId, evt.FriendPlayerId);
        await _roomStore.RemoveRoomAsync(channelId);

        // 접속 중인 쪽이 있으면 방이 무효화됐다고 알린다. 오프라인이면 다음에 GetMyRooms를
        // 불렀을 때 자연스럽게 안 보이게 되므로 별도 처리가 필요 없다.
        await NotifyIfConnectedAsync(evt.PlayerId, channelId);
        await NotifyIfConnectedAsync(evt.FriendPlayerId, channelId);

        _logger.LogInformation("친구 삭제로 채팅방 정리 완료: {PlayerId} ↔ {FriendPlayerId} (ChannelId={ChannelId})",
            evt.PlayerId, evt.FriendPlayerId, channelId);

        return true;
    }

    private async Task NotifyIfConnectedAsync(string playerId, string channelId)
    {
        var connection = _connections.TryGet(playerId);
        if (connection == null || connection.WebSocket.State != System.Net.WebSockets.WebSocketState.Open)
        {
            return;
        }

        var message = new ChatContracts.ServerChatMessage
        {
            RoomRemoved = new ChatContracts.RoomRemoved { ChannelId = channelId },
        };

        var bytes = Google.Protobuf.MessageExtensions.ToByteArray(message);
        await connection.WebSocket.SendAsync(
            new ArraySegment<byte>(bytes), System.Net.WebSockets.WebSocketMessageType.Binary,
            endOfMessage: true, CancellationToken.None);
    }
}

