using RabbitMQ.Client;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Collections.Concurrent;
using System.Text;

namespace MainServer.Common.Messaging;

/// <summary>
/// RabbitMQ 프로듀서. 채널 풀 방식으로 구현해 고부하에서도 병목 없이 동작한다.
///
/// IConnection은 스레드 안전하므로 하나를 공유한다.
/// IModel(채널)은 스레드 안전하지 않으므로 풀에서 빌려 쓰고 반납한다.
/// 이렇게 하면 발행이 직렬화되지 않고 여러 스레드가 동시에 발행할 수 있다.
///
/// 아웃박스 패턴은 호출하는 쪽에서 구현한다.
/// </summary>
public sealed class RabbitMqProducer : IDisposable
{
    private readonly RabbitMqProducerOptions _options;
    private readonly ILogger<RabbitMqProducer> _logger;

    // 연결은 하나만 공유 (IConnection은 스레드 안전)
    private IConnection? _connection;
    private readonly object _connectionLock = new();

    // 채널 풀 (IModel은 스레드 안전하지 않으므로 스레드별로 빌려 쓰고 반납)
    private readonly ConcurrentBag<IModel> _channelPool = new();

    public RabbitMqProducer(
        IOptions<RabbitMqProducerOptions> options,
        ILogger<RabbitMqProducer> logger)
    {
        _options = options.Value;
        _logger = logger;
    }

    /// <summary>
    /// 공유 연결을 가져온다. 없거나 끊겼으면 재연결한다.
    /// 연결 생성만 락으로 보호하고, 이후 채널 작업은 락 없이 진행한다.
    /// </summary>
    private IConnection GetOrCreateConnection()
    {
        if (_connection is { IsOpen: true })
            return _connection;

        lock (_connectionLock)
        {
            if (_connection is { IsOpen: true })
                return _connection;

            _logger.LogInformation("RabbitMQ 연결 시도: {Host}:{Port}", _options.Host, _options.Port);

            _connection?.Dispose();
            var factory = new ConnectionFactory
            {
                HostName = _options.Host,
                Port = _options.Port,
                UserName = _options.UserName,
                Password = _options.Password,
                VirtualHost = _options.VirtualHost,
            };
            _connection = factory.CreateConnection();
            _logger.LogInformation("RabbitMQ 연결 성공");
            return _connection;
        }
    }

    /// <summary>
    /// 채널 풀에서 채널을 빌린다. 풀이 비어있으면 새로 만든다.
    /// 사용 후 반드시 ReturnChannel로 반납해야 한다.
    /// </summary>
    private IModel RentChannel()
    {
        if (_channelPool.TryTake(out var channel) && channel.IsOpen)
            return channel;

        var connection = GetOrCreateConnection();
        var newChannel = connection.CreateModel();
        newChannel.ConfirmSelect();
        return newChannel;
    }

    /// <summary>사용한 채널을 풀에 반납한다. 채널이 닫혔으면 그냥 버린다.</summary>
    private void ReturnChannel(IModel channel)
    {
        if (channel.IsOpen)
            _channelPool.Add(channel);
        else
            channel.Dispose();
    }

    /// <summary>채널 작업 실패 시 채널을 버리고 연결도 무효화한다.</summary>
    private void DiscardChannel(IModel channel)
    {
        channel.Dispose();
        lock (_connectionLock)
        {
            _connection?.Dispose();
            _connection = null;
        }
        // 풀에 있는 채널들도 이미 끊긴 연결 기반이므로 전부 버린다
        while (_channelPool.TryTake(out var pooled))
            pooled.Dispose();
    }

    /// <summary>work queue 패턴으로 메시지를 발행한다.</summary>
    public void PublishToQueue(string queueName, string message)
    {
        var channel = RentChannel();
        try
        {
            var body = Encoding.UTF8.GetBytes(message);
            var props = channel.CreateBasicProperties();
            props.Persistent = true;

            channel.BasicPublish(exchange: string.Empty, routingKey: queueName, basicProperties: props, body: body);
            channel.WaitForConfirmsOrDie(TimeSpan.FromSeconds(5));

            _logger.LogDebug("RabbitMQ 발행 완료: Queue={Queue}", queueName);
            ReturnChannel(channel);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "RabbitMQ 발행 실패: Queue={Queue}", queueName);
            DiscardChannel(channel);
            throw;
        }
    }

    /// <summary>fanout exchange로 메시지를 발행한다 (모든 구독자에게 브로드캐스트).</summary>
    public void PublishToFanout(string exchangeName, string message)
    {
        var channel = RentChannel();
        try
        {
            var body = Encoding.UTF8.GetBytes(message);
            var props = channel.CreateBasicProperties();
            props.Persistent = true;

            channel.BasicPublish(exchange: exchangeName, routingKey: string.Empty, basicProperties: props, body: body);
            channel.WaitForConfirmsOrDie(TimeSpan.FromSeconds(5));

            _logger.LogDebug("RabbitMQ fanout 발행 완료: Exchange={Exchange}", exchangeName);
            ReturnChannel(channel);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "RabbitMQ fanout 발행 실패: Exchange={Exchange}", exchangeName);
            DiscardChannel(channel);
            throw;
        }
    }

    public void Dispose()
    {
        while (_channelPool.TryTake(out var channel))
            channel.Dispose();
        _connection?.Dispose();
    }
}
