using RabbitMQ.Client;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Text;

namespace MainServer.Common.Messaging;

/// <summary>
/// RabbitMQ 프로듀서 래퍼. 메인 서버(로비/인게임)가 메시지를 발행할 때 쓴다.
/// 아웃박스 패턴은 호출하는 쪽에서 구현한다.
/// </summary>
public sealed class RabbitMqProducer : IDisposable
{
    private readonly IConnection _connection;
    private readonly IModel _channel;
    private readonly ILogger<RabbitMqProducer> _logger;

    public RabbitMqProducer(
        IOptions<RabbitMqProducerOptions> options,
        ILogger<RabbitMqProducer> logger)
    {
        _logger = logger;
        var factory = new ConnectionFactory
        {
            HostName = options.Value.Host,
            Port = options.Value.Port,
            UserName = options.Value.UserName,
            Password = options.Value.Password,
            VirtualHost = options.Value.VirtualHost,
        };
        _connection = factory.CreateConnection();
        _channel = _connection.CreateModel();
        _channel.ConfirmSelect();
    }

    /// <summary>work queue 패턴으로 메시지를 발행한다 (특정 큐로 직접 전달).</summary>
    public void PublishToQueue(string queueName, string message)
    {
        _channel.QueueDeclare(queueName, durable: true, exclusive: false, autoDelete: false);

        var body = Encoding.UTF8.GetBytes(message);
        var props = _channel.CreateBasicProperties();
        props.Persistent = true;

        _channel.BasicPublish(exchange: string.Empty, routingKey: queueName, basicProperties: props, body: body);
        _channel.WaitForConfirmsOrDie(TimeSpan.FromSeconds(5));

        _logger.LogDebug("RabbitMQ 발행 완료: Queue={Queue}", queueName);
    }

    /// <summary>fanout exchange로 메시지를 발행한다 (모든 구독자에게 브로드캐스트).</summary>
    public void PublishToFanout(string exchangeName, string message)
    {
        _channel.ExchangeDeclare(exchangeName, ExchangeType.Fanout, durable: true);

        var body = Encoding.UTF8.GetBytes(message);
        var props = _channel.CreateBasicProperties();
        props.Persistent = true;

        _channel.BasicPublish(exchange: exchangeName, routingKey: string.Empty, basicProperties: props, body: body);
        _channel.WaitForConfirmsOrDie(TimeSpan.FromSeconds(5));

        _logger.LogDebug("RabbitMQ fanout 발행 완료: Exchange={Exchange}", exchangeName);
    }

    public void Dispose()
    {
        _channel.Dispose();
        _connection.Dispose();
    }
}
