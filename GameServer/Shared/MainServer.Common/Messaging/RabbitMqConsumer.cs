using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace MainServer.Common.Messaging;

/// <summary>
/// RabbitMQ 컨슈머 - 수동 ack만 지원한다(RabbitMQ 기본값이기도 하고, 안전한 방식이라
/// 자동 ack는 아예 선택지로 안 둠). 메시지를 받아 콜백을 실행하고, 콜백이 성공적으로
/// 끝나야만 ack를 보낸다. 실패하면(예외 또는 false 반환) nack(재큐잉)한다 -
/// 컨슈머가 처리 도중 죽어도(연결 끊김) RabbitMQ가 감지해서 다른 컨슈머(또는 재시작
/// 후 같은 컨슈머)에게 자동으로 재전달한다.
///
/// 이게 보장하는 건 "최소 한 번" 처리다 - "정확히 한 번"이 아니다. 재시도/재연결로
/// 같은 메시지가 두 번 이상 처리될 수 있으므로, 콜백은 반드시 멱등해야 한다(같은
/// 이벤트를 두 번 받아도 결과가 같아야 함 - 예: 이미 없는 걸 또 지우려 하면 조용히
/// 무시).
///
/// 실패 시 무조건 재큐잉하는 점에 주의 - 메시지 자체가 영구적으로 처리 불가능한
/// 경우(예: 파싱 불가능한 형식)라면 무한 재전달 루프가 될 수 있다. 지금은 이벤트
/// 스키마가 내부적으로 통제되는 단순한 상황이라 위험이 낮다고 보고 단순하게 갔지만,
/// 나중에 문제가 되면 데드레터 익스체인지(dead-letter exchange)로 재시도 횟수 제한을
/// 두는 걸 고려할 것.
/// </summary>
public sealed class RabbitMqConsumer : IDisposable
{
    private readonly RabbitMqProducerOptions _options;
    private readonly ILogger<RabbitMqConsumer> _logger;

    private IConnection? _connection;
    private IModel? _channel;

    public RabbitMqConsumer(IOptions<RabbitMqProducerOptions> options, ILogger<RabbitMqConsumer> logger)
    {
        _options = options.Value;
        _logger = logger;
    }

    /// <summary>
    /// 큐를 구독한다. onMessage가 true를 반환하면 ack, false를 반환하거나 예외를 던지면
    /// nack(재큐잉)한다. prefetchCount=1로 둬서 "처리 끝나야 다음 메시지를 받는다" -
    /// 한 번에 여러 메시지를 동시에 처리하지 않는다(단순함 우선, 처리량이 문제되면
    /// 나중에 늘릴 것).
    /// </summary>
    public void Subscribe(string queueName, Func<string, Task<bool>> onMessage)
    {
        var factory = new ConnectionFactory
        {
            HostName = _options.Host,
            Port = _options.Port,
            UserName = _options.UserName,
            Password = _options.Password,
            VirtualHost = _options.VirtualHost,
        };

        var connection = factory.CreateConnection();
        var channel = connection.CreateModel();
        channel.QueueDeclare(queue: queueName, durable: true, exclusive: false, autoDelete: false);
        channel.BasicQos(prefetchSize: 0, prefetchCount: 1, global: false);

        _connection = connection;
        _channel = channel;

        var consumer = new EventingBasicConsumer(channel);
        consumer.Received += async (_, ea) =>
        {
            var message = Encoding.UTF8.GetString(ea.Body.ToArray());

            try
            {
                var handled = await onMessage(message);
                if (handled)
                {
                    channel.BasicAck(ea.DeliveryTag, multiple: false);
                }
                else
                {
                    _logger.LogWarning("메시지 처리 실패(false 반환) - 재큐잉: Queue={Queue}", queueName);
                    channel.BasicNack(ea.DeliveryTag, multiple: false, requeue: true);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "메시지 처리 중 예외 - 재큐잉: Queue={Queue}", queueName);
                channel.BasicNack(ea.DeliveryTag, multiple: false, requeue: true);
            }
        };

        channel.BasicConsume(queue: queueName, autoAck: false, consumer: consumer);
        _logger.LogInformation("RabbitMQ 큐 구독 시작: {Queue}", queueName);
    }

    public void Dispose()
    {
        _channel?.Dispose();
        _connection?.Dispose();
    }
}
