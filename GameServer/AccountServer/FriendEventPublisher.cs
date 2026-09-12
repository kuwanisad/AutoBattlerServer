using System.Text.Json;
using MainServer.Common.Messaging;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AccountServer;

/// <summary>
/// FriendEventOutbox를 계속 훑으면서 RabbitMQ에 실제 발행을 시도한다. 발행이 확실히
/// 성공해야만(RabbitMqProducer.PublishToQueue가 예외 없이 끝나야만, 즉 브로커의 발행
/// 확인까지 받아야만) 큐에서 뺀다 - 실패하면 그대로 큐에 남겨두고 잠시 후 같은
/// 이벤트로 다시 시도한다.
/// </summary>
public sealed class FriendEventPublisher : BackgroundService
{
    private const string FriendRemovedQueueName = "friend-removed";
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan PollDelay = TimeSpan.FromMilliseconds(200);

    private readonly FriendEventOutbox _outbox;
    private readonly RabbitMqProducer _producer;
    private readonly ILogger<FriendEventPublisher> _logger;

    public FriendEventPublisher(
        FriendEventOutbox outbox,
        RabbitMqProducer producer,
        ILogger<FriendEventPublisher> logger)
    {
        _outbox = outbox;
        _producer = producer;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            if (_outbox.TryPeek(out var evt))
            {
                try
                {
                    var json = JsonSerializer.Serialize(evt);
                    _producer.PublishToQueue(FriendRemovedQueueName, json);

                    // 발행이 확실히 성공했을 때만 뺀다 - PublishToQueue는 브로커의 발행
                    // 확인(WaitForConfirmsOrDie)까지 받은 뒤에야 정상 반환한다.
                    _outbox.TryDequeue(out _);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "친구 삭제 이벤트 발행 실패 - 잠시 후 재시도: {PlayerId}/{FriendPlayerId}",
                        evt.PlayerId, evt.FriendPlayerId);
                    await Task.Delay(RetryDelay, stoppingToken);
                }
            }
            else
            {
                await Task.Delay(PollDelay, stoppingToken);
            }
        }
    }
}