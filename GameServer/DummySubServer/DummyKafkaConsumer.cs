using Confluent.Kafka;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SubServer.Common.Messaging;

namespace DummySubServer;

public sealed class DummyKafkaConsumer : KafkaConsumerBase<string, string>
{
    private readonly ILogger<DummyKafkaConsumer> _logger;

    public DummyKafkaConsumer(
        IOptions<KafkaConsumerOptions> options,
        ILogger<DummyKafkaConsumer> logger)
        : base(options, logger)
    {
        _logger = logger;
    }

    protected override Task HandleMessageAsync(
        ConsumeResult<string, string> result,
        CancellationToken cancellationToken)
    {
        _logger.LogInformation(
            "[Kafka] 메시지 수신 - Topic={Topic} Partition={Partition} Offset={Offset} Value={Value}",
            result.Topic, result.Partition, result.Offset, result.Message.Value);

        return Task.CompletedTask;
    }
}
