using Confluent.Kafka;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace SubServer.Common.Messaging;

/// <summary>
/// Kafka 컨슈머 베이스 클래스. 하위 서버가 이를 상속해서 HandleMessageAsync만 구현하면
/// 연결/재시도/오프셋 커밋 등을 공통으로 처리한다.
///
/// at-least-once 보장: 메시지 처리 완료 후 오프셋을 커밋한다.
/// HandleMessageAsync는 반드시 멱등성을 보장해야 한다 (인박스 패턴 권장).
/// </summary>
public abstract class KafkaConsumerBase<TKey, TValue> : BackgroundService
{
    private readonly KafkaConsumerOptions _options;
    private readonly ILogger _logger;

    protected KafkaConsumerBase(
        IOptions<KafkaConsumerOptions> options,
        ILogger logger)
    {
        _options = options.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var config = new ConsumerConfig
        {
            BootstrapServers = _options.BootstrapServers,
            GroupId = _options.GroupId,
            AutoOffsetReset = _options.AutoOffsetReset == "earliest"
                ? AutoOffsetReset.Earliest
                : AutoOffsetReset.Latest,
            EnableAutoCommit = false,
        };

        using var consumer = new ConsumerBuilder<TKey, TValue>(config)
            .SetErrorHandler((_, e) =>
                _logger.LogError("Kafka 컨슈머 오류: {Reason} (IsFatal={IsFatal})", e.Reason, e.IsFatal))
            .Build();

        consumer.Subscribe(_options.Topics);
        _logger.LogInformation("Kafka 구독 시작: {Topics} (GroupId={GroupId})",
            string.Join(", ", _options.Topics), _options.GroupId);

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                ConsumeResult<TKey, TValue>? result = null;
                try
                {
                    result = consumer.Consume(TimeSpan.FromMilliseconds(100));
                    if (result is null) continue;

                    _logger.LogDebug("메시지 수신: Topic={Topic} Partition={Partition} Offset={Offset}",
                        result.Topic, result.Partition, result.Offset);

                    await HandleMessageAsync(result, stoppingToken);
                    consumer.Commit(result);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "메시지 처리 중 오류. Topic={Topic} Offset={Offset}",
                        result?.Topic, result?.Offset);
                }
            }
        }
        finally
        {
            consumer.Close();
            _logger.LogInformation("Kafka 컨슈머 종료: {GroupId}", _options.GroupId);
        }
    }

    /// <summary>
    /// 메시지 하나를 처리한다. 하위 서버가 구현한다.
    /// 멱등성을 보장해야 한다 — 같은 메시지가 두 번 와도 결과가 같아야 한다.
    /// </summary>
    protected abstract Task HandleMessageAsync(
        ConsumeResult<TKey, TValue> result,
        CancellationToken cancellationToken);
}
