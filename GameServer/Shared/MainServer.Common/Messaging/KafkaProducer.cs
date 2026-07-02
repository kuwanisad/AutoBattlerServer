using Confluent.Kafka;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MainServer.Common.Messaging;

/// <summary>
/// Kafka 프로듀서 래퍼. 메인 서버(로비/인게임)가 이벤트를 발행할 때 쓴다.
/// 아웃박스 패턴은 호출하는 쪽에서 구현한다. 이 클래스는 실제 전송만 담당한다.
/// </summary>
public sealed class KafkaProducer<TKey, TValue> : IDisposable
{
    private readonly IProducer<TKey, TValue> _producer;
    private readonly ILogger<KafkaProducer<TKey, TValue>> _logger;

    public KafkaProducer(
        IOptions<KafkaProducerOptions> options,
        ILogger<KafkaProducer<TKey, TValue>> logger)
    {
        _logger = logger;
        var config = new ProducerConfig
        {
            BootstrapServers = options.Value.BootstrapServers,
            EnableIdempotence = true,
            Acks = Acks.All,
        };
        _producer = new ProducerBuilder<TKey, TValue>(config).Build();
    }

    /// <summary>
    /// 메시지를 발행하고 브로커의 ack를 확인한 뒤 반환한다.
    /// ack 확인 후에야 아웃박스 row를 삭제해야 한다 (호출하는 쪽 책임).
    /// </summary>
    public async Task<DeliveryResult<TKey, TValue>> ProduceAsync(
        string topic,
        Message<TKey, TValue> message,
        CancellationToken cancellationToken = default)
    {
        var result = await _producer.ProduceAsync(topic, message, cancellationToken);

        _logger.LogDebug("Kafka 발행 완료: Topic={Topic} Partition={Partition} Offset={Offset}",
            result.Topic, result.Partition, result.Offset);

        return result;
    }

    public void Dispose() => _producer.Dispose();
}
