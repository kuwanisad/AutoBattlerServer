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
/// 채팅방을 정리한다. RabbitMqConsumer의 수동 ack 방식을 그대로 쓴다 - 처리(방 삭제)가
/// 확실히 끝나야만 ack를 보내고, 실패하면 재큐잉돼서 나중에 다시 시도된다.
///
/// [미구현] 실제 "둘만의 채팅방을 찾아서 지우는" 로직은 아직 없다 - 채팅방 생성/관리
/// 자체가 이번 라운드에서 범위 밖으로 미뤄졌기 때문(계정 정보 확장부터 먼저 하기로 함).
/// 지금은 이벤트를 받아서 로그만 남기고 ack하는 자리표시자 - 배선(발행→구독→ack)
/// 자체가 끝까지 동작하는지만 검증하는 용도.
/// </summary>
public sealed class FriendRemovedSubscriber : IHostedService
{
    private const string FriendRemovedQueueName = "friend-removed";

    private readonly RabbitMqConsumer _consumer;
    private readonly ILogger<FriendRemovedSubscriber> _logger;

    public FriendRemovedSubscriber(RabbitMqConsumer consumer, ILogger<FriendRemovedSubscriber> logger)
    {
        _consumer = consumer;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _consumer.Subscribe(FriendRemovedQueueName, HandleAsync);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private Task<bool> HandleAsync(string json)
    {
        FriendRemovedEvent? evt;
        try
        {
            evt = JsonSerializer.Deserialize<FriendRemovedEvent>(json);
        }
        catch (JsonException ex)
        {
            // 파싱 자체가 불가능한 메시지 - 재큐잉해봤자 계속 실패할 게 뻔하므로(영구적
            // 실패) true를 반환해서 ack시키고 버린다. false/예외를 던지면 무한 재전달
            // 루프가 될 위험이 있다.
            _logger.LogError(ex, "친구 삭제 이벤트 파싱 실패 - 버림: {Json}", json);
            return Task.FromResult(true);
        }

        if (evt == null || string.IsNullOrEmpty(evt.PlayerId) || string.IsNullOrEmpty(evt.FriendPlayerId))
        {
            _logger.LogError("친구 삭제 이벤트 필드 누락 - 버림: {Json}", json);
            return Task.FromResult(true);
        }

        // TODO: 둘만의 채팅방(friend:{friendshipId} 채널)을 찾아서 실제로 정리하는 로직.
        // 지금은 배선 검증용 로그만 남긴다.
        _logger.LogInformation("친구 삭제 이벤트 수신 (방 정리 로직 미구현): {PlayerId} ↔ {FriendPlayerId}",
            evt.PlayerId, evt.FriendPlayerId);

        return Task.FromResult(true);
    }
}
