using RabbitMQ.Client.Events;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SubServer.Common.Messaging;

namespace DummySubServer;

public sealed class DummyRabbitMqConsumer : RabbitMqConsumerBase
{
    private readonly ILogger<DummyRabbitMqConsumer> _logger;

    public DummyRabbitMqConsumer(
        IOptions<RabbitMqConsumerOptions> options,
        ILogger<DummyRabbitMqConsumer> logger)
        : base(options, logger)
    {
        _logger = logger;
    }

    protected override Task HandleMessageAsync(
        BasicDeliverEventArgs ea,
        CancellationToken cancellationToken)
    {
        var body = GetBody(ea);
        _logger.LogInformation(
            "[RabbitMQ] 메시지 수신 - Queue={Queue} DeliveryTag={Tag} Body={Body}",
            ea.RoutingKey, ea.DeliveryTag, body);

        return Task.CompletedTask;
    }
}
