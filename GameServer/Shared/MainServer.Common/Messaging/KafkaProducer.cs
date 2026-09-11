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
            // 멱등 프로듀서 - 재시도 시 중복 발행 방지
            EnableIdempotence = true,
            // 모든 복제본에서 ack를 받아야 완료로 간주 (데이터 안전성 최대화)
            Acks = Acks.All,
            // 재시도 횟수와 간격을 명시적으로 설정
            // EnableIdempotence가 true면 재시도는 자동 활성화되지만,
            // 고부하 환경에서 재시도 폭풍을 방지하기 위해 간격을 명시한다.
            MessageSendMaxRetries = 3,
            RetryBackoffMs = 200,
            // 배치 처리로 처리량 향상 - 최대 5ms 대기 후 한 번에 전송
            LingerMs = 5,
            // 배치 크기 최대화 (기본값 1MB → 4MB)
            BatchSize = 4 * 1024 * 1024,
            // 브로커에 아예 연결이 안 되는 상황(다운 등)을 대비한 상한선.
            // 기본값(5분)은 "죽었는지 확인"하는 사람 입장에선 사실상 무한 대기처럼
            // 보인다 - 재시도는 하되 10초 안에는 반드시 실패로 확정 짓는다.
            MessageTimeoutMs = 10000,
        };
        _producer = new ProducerBuilder<TKey, TValue>(config).Build();
    }

    /// <summary>
    /// 메시지를 발행하고 브로커의 ack를 확인한 뒤 반환한다.
    /// ack 확인 후에야 아웃박스 row를 삭제해야 한다 (호출하는 쪽 책임).
    ///
    /// EnableIdempotence + Acks.All 설정으로 Kafka 클라이언트가 내부적으로 재시도를 처리한다.
    /// 예외가 던져졌다면 이미 재시도를 다 해봤거나 재시도 불가능한 실패다.
    /// </summary>
    public async Task<DeliveryResult<TKey, TValue>> ProduceAsync(
        string topic,
        Message<TKey, TValue> message,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var result = await _producer.ProduceAsync(topic, message, cancellationToken);

            _logger.LogDebug("Kafka 발행 완료: Topic={Topic} Partition={Partition} Offset={Offset}",
                result.Topic, result.Partition, result.Offset);

            return result;
        }
        catch (ProduceException<TKey, TValue> ex)
        {
            // Kafka 전송 실패 - 재시도 가능 여부를 구분해서 로그를 남긴다.
            // 아웃박스 패턴을 쓰는 경우 호출하는 쪽에서 이 예외를 잡아 row를 유지해야 한다.
            if (ex.Error.IsFatal)
            {
                _logger.LogCritical(ex,
                    "Kafka 치명적 오류 (재시도 불가): Topic={Topic} Code={Code} Reason={Reason}",
                    topic, ex.Error.Code, ex.Error.Reason);
            }
            else
            {
                _logger.LogError(ex,
                    "Kafka 전송 실패: Topic={Topic} Code={Code} Reason={Reason}",
                    topic, ex.Error.Code, ex.Error.Reason);
            }
            throw;
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("Kafka 발행 취소됨: Topic={Topic}", topic);
            throw;
        }
    }

    public void Dispose() => _producer.Dispose();
}