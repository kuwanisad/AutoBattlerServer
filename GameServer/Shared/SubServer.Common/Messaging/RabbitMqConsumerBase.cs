using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Text;

namespace SubServer.Common.Messaging;

/// <summary>
/// RabbitMQ 컨슈머 베이스 클래스. 하위 서버가 이를 상속해서 HandleMessageAsync만 구현하면
/// 연결/채널/큐 선언/재시도 등을 공통으로 처리한다.
///
/// at-least-once 보장: HandleMessageAsync 성공 시 Ack, 실패 시 Nack(재큐잉 안 함).
/// HandleMessageAsync는 반드시 멱등성을 보장해야 한다 (인박스 패턴 권장).
/// </summary>
public abstract class RabbitMqConsumerBase : BackgroundService
{
    private readonly RabbitMqConsumerOptions _options;
    protected readonly ILogger Logger;

    protected RabbitMqConsumerBase(
        IOptions<RabbitMqConsumerOptions> options,
        ILogger logger)
    {
        _options = options.Value;
        Logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunConsumerAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "RabbitMQ 연결 오류. 5초 후 재시도합니다.");
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
        }
    }

    private async Task RunConsumerAsync(CancellationToken stoppingToken)
    {
        var factory = new ConnectionFactory
        {
            HostName = _options.Host,
            Port = _options.Port,
            UserName = _options.UserName,
            Password = _options.Password,
            VirtualHost = _options.VirtualHost,
            DispatchConsumersAsync = true,
        };

        using var connection = factory.CreateConnection();
        using var channel = connection.CreateModel();

        channel.QueueDeclare(
            queue: _options.QueueName,
            durable: true,
            exclusive: false,
            autoDelete: false);

        if (!string.IsNullOrEmpty(_options.ExchangeName))
        {
            channel.ExchangeDeclare(_options.ExchangeName, ExchangeType.Fanout, durable: true);
            channel.QueueBind(_options.QueueName, _options.ExchangeName, routingKey: string.Empty);
        }

        channel.BasicQos(prefetchSize: 0, prefetchCount: _options.PrefetchCount, global: false);

        var consumer = new AsyncEventingBasicConsumer(channel);
        consumer.Received += async (_, ea) =>
        {
            try
            {
                await HandleMessageAsync(ea, stoppingToken);
                channel.BasicAck(ea.DeliveryTag, multiple: false);
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "메시지 처리 실패. Queue={Queue} DeliveryTag={Tag}",
                    _options.QueueName, ea.DeliveryTag);
                channel.BasicNack(ea.DeliveryTag, multiple: false, requeue: false);
            }
        };

        channel.BasicConsume(_options.QueueName, autoAck: false, consumer: consumer);
        Logger.LogInformation("RabbitMQ 구독 시작: Queue={Queue}", _options.QueueName);

        await Task.Delay(Timeout.Infinite, stoppingToken);
    }

    /// <summary>
    /// 메시지 하나를 처리한다. 하위 서버가 구현한다.
    /// 멱등성을 보장해야 한다 — 같은 메시지가 두 번 와도 결과가 같아야 한다.
    /// </summary>
    protected abstract Task HandleMessageAsync(
        BasicDeliverEventArgs ea,
        CancellationToken cancellationToken);

    /// <summary>메시지 본문을 UTF-8 문자열로 변환하는 헬퍼.</summary>
    protected static string GetBody(BasicDeliverEventArgs ea) =>
        Encoding.UTF8.GetString(ea.Body.Span);
}
