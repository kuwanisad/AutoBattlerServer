using System.Collections.Concurrent;
using System.Text;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using RabbitMQ.Client.Exceptions;

namespace Shared.Common.Messaging;

/// <summary>구독 옵션. 기본값은 메인 서버 쪽 예전 RabbitMqConsumer와 같다.</summary>
public sealed class RabbitMqSubscribeOptions
{
    /// <summary>처리 완료(ack) 전까지 한 번에 받아둘 메시지 수.</summary>
    public ushort PrefetchCount { get; init; } = 1;

    /// <summary>
    /// 처리 실패(false 반환 또는 예외) 시 큐에 되돌릴지. true면 나중에 다시 전달되고, false면 버려진다.
    /// 메인 서버 컨슈머는 true(일시적 실패 가정), 하위 서버 컨슈머 베이스는 false(예전 동작 유지).
    /// </summary>
    public bool RequeueOnFailure { get; init; } = true;

    /// <summary>비어있지 않으면 이 fanout exchange에 큐를 바인딩한다(브로드캐스트 수신).</summary>
    public string FanoutExchange { get; init; } = string.Empty;
}

/// <summary>
/// RabbitMQ 중개 클래스 - 접속, 발행, 구독을 한 곳에서 맡는다. 메인 서버와 하위 서버가 같은 클래스를 쓴다.
///
/// 접속: 서버 시작을 막지 않고 백그라운드에서 접속한다. 실패하거나 끊기면 성공할 때까지
///   RabbitMqOptions.ReconnectInterval마다 다시 시도한다(ReconnectPolicy). 라이브러리 자체 자동
///   복구는 끄고 이 루프 하나로만 재접속한다 - 재접속 경로가 둘이면 상태가 꼬이기 쉽다.
///
/// 발행(PublishToQueue/PublishToFanout): 대상별 버퍼(OutboundBuffer)에 넣고 즉시 반환한다 - 호출한
///   게임 처리(로그아웃 등)를 막지 않는다. 접속돼 있으면 백그라운드가 바로 보내고, 끊겨 있으면 쌓아뒀다가
///   접속되면 순서대로 보낸다. 브로커의 발행 확인(publisher confirm)을 받아야만 버퍼에서 뺀다 -
///   보내다 끊기면 다음 연결에서 같은 메시지부터 다시 보낸다(같은 메시지가 두 번 갈 수 있으므로
///   받는 쪽은 멱등해야 한다).
///   보내기 전에 큐를 선언한다 - 받는 쪽이 아직 한 번도 안 붙어서 큐가 없을 때 메시지가 조용히
///   사라지지 않게(선언은 여러 번 해도 결과가 같다).
///
/// 구독(Subscribe): 등록해 두면 접속/재접속될 때마다 구독을 다시 연다.
///
/// 한계: 버퍼는 메모리에만 있어서 프로세스가 죽으면 쌓인 메시지는 사라진다.
/// </summary>
public sealed class RabbitMqBroker : BackgroundService
{
    private static readonly TimeSpan ConfirmTimeout = TimeSpan.FromSeconds(5);

    private readonly RabbitMqOptions _options;
    private readonly ILogger<RabbitMqBroker> _logger;
    private readonly string _clientName;

    private readonly ConcurrentDictionary<string, OutboundBuffer<OutboundMessage>> _buffers = new();
    private readonly SemaphoreSlim _outboundSignal = new(0);

    private readonly object _subscriptionsLock = new();
    private readonly List<Subscription> _subscriptions = new();

    // 지금 연결(없으면 null). 구독 등록 시점에 이미 연결돼 있으면 바로 구독을 열기 위해 들고 있는다.
    private volatile IConnection? _connection;

    public RabbitMqBroker(IOptions<RabbitMqOptions> options, ILogger<RabbitMqBroker> logger)
    {
        _options = options.Value;
        _logger = logger;
        _clientName = System.Reflection.Assembly.GetEntryAssembly()?.GetName().Name ?? "server";
    }

    /// <summary>지금 RabbitMQ에 연결돼 있는지.</summary>
    public bool IsConnected => _connection is { IsOpen: true };

    /// <summary>아직 보내지 못하고 쌓여 있는 메시지 수(전체).</summary>
    public int PendingCount => _buffers.Values.Sum(b => b.Count);

    // ================================ 발행 ================================

    /// <summary>work queue로 발행한다(버퍼에 넣고 즉시 반환).</summary>
    public void PublishToQueue(string queueName, string message) =>
        Enqueue(new OutboundMessage(OutboundKind.Queue, queueName, message));

    /// <summary>fanout exchange로 발행한다(모든 구독자에게 브로드캐스트, 버퍼에 넣고 즉시 반환).</summary>
    public void PublishToFanout(string exchangeName, string message) =>
        Enqueue(new OutboundMessage(OutboundKind.Fanout, exchangeName, message));

    private void Enqueue(OutboundMessage message)
    {
        var buffer = _buffers.GetOrAdd(message.Target, target => new OutboundBuffer<OutboundMessage>(_options.GetBufferLimit(target)));
        var dropped = buffer.Enqueue(message);

        if (dropped > 0)
        {
            _logger.LogWarning("RabbitMQ 발행 버퍼 한도({Limit}) 초과 - 오래된 메시지 {Dropped}개 버림: {Target}",
                buffer.Limit, dropped, message.Target);
        }
        else if (!IsConnected)
        {
            var count = buffer.Count;
            // 연결이 없는 동안 쌓이는 상황을 알 수 있게 - 1개째와 이후 1000개마다만 남긴다.
            if (count == 1 || count % 1000 == 0)
            {
                _logger.LogWarning("RabbitMQ 연결 없음 - 발행 대기 중: {Target} {Count}개", message.Target, count);
            }
        }

        _outboundSignal.Release();
    }

    // ================================ 구독 ================================

    /// <summary>
    /// 큐를 구독한다. onMessage가 true를 반환하면 ack, false를 반환하거나 예외를 던지면 nack
    /// (options.RequeueOnFailure에 따라 재큐잉 여부 결정). 콜백은 같은 메시지를 두 번 받아도
    /// 결과가 같아야 한다(최소 한 번 전달). 연결 전에 불러도 되고, 접속/재접속될 때마다 자동으로 다시 열린다.
    /// </summary>
    public void Subscribe(string queueName, Func<string, Task<bool>> onMessage, RabbitMqSubscribeOptions? options = null) =>
        SubscribeRaw(queueName, ea => onMessage(Encoding.UTF8.GetString(ea.Body.Span)), options);

    /// <summary>Subscribe와 같지만 RabbitMQ 원본 전달 정보(라우팅 키, 헤더 등)를 그대로 받는다.</summary>
    public void SubscribeRaw(string queueName, Func<BasicDeliverEventArgs, Task<bool>> onMessage, RabbitMqSubscribeOptions? options = null)
    {
        var subscription = new Subscription(queueName, onMessage, options ?? new RabbitMqSubscribeOptions());
        lock (_subscriptionsLock) _subscriptions.Add(subscription);

        var connection = _connection;
        if (connection is { IsOpen: true })
        {
            try { StartSubscription(connection, subscription); }
            catch (Exception ex)
            {
                // 연결이 막 끊기던 참이면 실패할 수 있다 - 재접속 때 다시 열린다.
                _logger.LogWarning(ex, "RabbitMQ 구독 시작 실패 - 재접속 때 다시 시도: {Queue}", queueName);
            }
        }
    }

    // ================================ 연결 유지 루프 ================================

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var factory = new ConnectionFactory
        {
            HostName = _options.Host,
            Port = _options.Port,
            UserName = _options.UserName,
            Password = _options.Password,
            VirtualHost = _options.VirtualHost,
            DispatchConsumersAsync = true,
            // 재접속은 이 클래스의 루프 하나로만 한다(위 클래스 주석 참고).
            AutomaticRecoveryEnabled = false,
            TopologyRecoveryEnabled = false,
        };
        var target = $"RabbitMQ({_options.Host}:{_options.Port})";

        while (!stoppingToken.IsCancellationRequested)
        {
            IConnection connection;
            try
            {
                connection = await ReconnectPolicy.ConnectUntilSuccessAsync(
                    () => factory.CreateConnection(_clientName), _options.ReconnectInterval, _logger, target, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            using var connectionLost = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            connection.ConnectionShutdown += (_, args) =>
            {
                if (!stoppingToken.IsCancellationRequested)
                {
                    _logger.LogWarning("{Target} 연결 끊김 - 재접속 시도: {Reason}", target, args.ReplyText);
                }
                try { connectionLost.Cancel(); } catch (ObjectDisposedException) { }
            };

            _connection = connection;
            try
            {
                StartAllSubscriptions(connection);

                var pending = PendingCount;
                if (pending > 0) _logger.LogInformation("RabbitMQ 연결됨 - 쌓여 있던 메시지 {Count}개 발행 시작", pending);

                await FlushLoopAsync(connection, connectionLost.Token);
            }
            catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
            {
                // 연결 끊김(ConnectionShutdown) - 아래에서 정리하고 다시 접속한다.
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                _logger.LogWarning(ex, "{Target} 발행 중 연결 문제 - 재접속", target);
            }
            catch (OperationCanceledException)
            {
                // 서버 종료
            }
            finally
            {
                _connection = null;
                StopAllSubscriptions();
                try { connection.Dispose(); } catch { /* 이미 끊김 */ }
            }
        }
    }

    /// <summary>
    /// 버퍼에 쌓인 메시지를 대상별로 순서대로 보낸다. 대상 하나가 설정 문제(예: 큐 선언 인자가 기존 큐와
    /// 다름)로 거부되면 그 대상만 잠시 보류하고 나머지 대상은 계속 보낸다 - 메시지는 버리지 않는다.
    /// 연결 자체가 끊기면 예외로 빠져나가 재접속한다.
    /// </summary>
    private async Task FlushLoopAsync(IConnection connection, CancellationToken ct)
    {
        var declared = new HashSet<string>();
        var blockedUntil = new Dictionary<string, DateTime>();
        IModel? channel = null;

        try
        {
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                var sentAny = false;

                foreach (var (targetKey, buffer) in _buffers)
                {
                    if (blockedUntil.TryGetValue(targetKey, out var until) && DateTime.UtcNow < until) continue;

                    while (buffer.TryPeek(out var message) && message != null)
                    {
                        ct.ThrowIfCancellationRequested();
                        channel ??= CreateConfirmChannel(connection);

                        try
                        {
                            Publish(channel, message, declared);
                        }
                        catch (OperationInterruptedException ex) when (connection.IsOpen && IsPermanentChannelError(ex))
                        {
                            // 채널만 닫힘(연결은 살아있음) - 이 대상의 설정 문제. 대상만 보류하고 계속.
                            _logger.LogError(ex, "RabbitMQ가 발행을 거부함 - {Target} 대상만 {Seconds}초 보류(메시지는 유지): {Reply}",
                                message.Target, _options.ReconnectInterval.TotalSeconds, ex.ShutdownReason?.ReplyText);
                            blockedUntil[targetKey] = DateTime.UtcNow + _options.ReconnectInterval;
                            declared.Remove(message.Target);
                            channel.Dispose();
                            channel = null;
                            break;
                        }

                        buffer.RemoveIfHead(message);
                        sentAny = true;
                    }
                }

                if (!sentAny)
                {
                    // 새 메시지가 오거나, 보류된 대상을 다시 볼 때가 되면 깨어난다.
                    await _outboundSignal.WaitAsync(blockedUntil.Count > 0 ? _options.ReconnectInterval : Timeout.InfiniteTimeSpan, ct);
                    blockedUntil = blockedUntil.Where(kv => DateTime.UtcNow < kv.Value).ToDictionary(kv => kv.Key, kv => kv.Value);
                }
            }
        }
        finally
        {
            try { channel?.Dispose(); } catch { /* 이미 끊김 */ }
        }
    }

    private static IModel CreateConfirmChannel(IConnection connection)
    {
        var channel = connection.CreateModel();
        channel.ConfirmSelect();
        return channel;
    }

    private void Publish(IModel channel, OutboundMessage message, HashSet<string> declared)
    {
        if (declared.Add(message.Target))
        {
            // 받는 쪽(구독), BrokerInitializer와 같은 인자로 선언해야 한다(다르면 RabbitMQ가 거부).
            if (message.Kind == OutboundKind.Queue)
                channel.QueueDeclare(message.Target, durable: true, exclusive: false, autoDelete: false);
            else
                channel.ExchangeDeclare(message.Target, ExchangeType.Fanout, durable: true);
        }

        var props = channel.CreateBasicProperties();
        props.Persistent = true;
        var body = Encoding.UTF8.GetBytes(message.Body);

        if (message.Kind == OutboundKind.Queue)
            channel.BasicPublish(exchange: string.Empty, routingKey: message.Target, basicProperties: props, body: body);
        else
            channel.BasicPublish(exchange: message.Target, routingKey: string.Empty, basicProperties: props, body: body);

        channel.WaitForConfirmsOrDie(ConfirmTimeout);
        _logger.LogDebug("RabbitMQ 발행 완료: {Kind} {Target}", message.Kind, message.Target);
    }

    // 404(대상 없음), 406(선언 인자 불일치), 403(권한) - 다시 보내도 같은 결과인 채널 단위 거부.
    private static bool IsPermanentChannelError(OperationInterruptedException ex) =>
        ex.ShutdownReason?.ReplyCode is 403 or 404 or 406;

    private void StartAllSubscriptions(IConnection connection)
    {
        Subscription[] snapshot;
        lock (_subscriptionsLock) snapshot = _subscriptions.ToArray();

        foreach (var subscription in snapshot)
        {
            StartSubscription(connection, subscription);
        }
    }

    private void StartSubscription(IConnection connection, Subscription subscription)
    {
        lock (subscription)
        {
            if (subscription.Channel is { IsOpen: true }) return;

            var channel = connection.CreateModel();
            var options = subscription.Options;

            channel.QueueDeclare(subscription.QueueName, durable: true, exclusive: false, autoDelete: false);
            if (!string.IsNullOrEmpty(options.FanoutExchange))
            {
                channel.ExchangeDeclare(options.FanoutExchange, ExchangeType.Fanout, durable: true);
                channel.QueueBind(subscription.QueueName, options.FanoutExchange, routingKey: string.Empty);
            }
            channel.BasicQos(prefetchSize: 0, prefetchCount: options.PrefetchCount, global: false);

            var consumer = new AsyncEventingBasicConsumer(channel);
            consumer.Received += async (_, ea) =>
            {
                bool handled;
                try
                {
                    handled = await subscription.OnMessage(ea);
                    if (!handled)
                    {
                        _logger.LogWarning("RabbitMQ 메시지 처리 실패(false 반환): Queue={Queue} 재큐잉={Requeue}",
                            subscription.QueueName, options.RequeueOnFailure);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "RabbitMQ 메시지 처리 중 예외: Queue={Queue} 재큐잉={Requeue}",
                        subscription.QueueName, options.RequeueOnFailure);
                    handled = false;
                }

                try
                {
                    if (handled) channel.BasicAck(ea.DeliveryTag, multiple: false);
                    else channel.BasicNack(ea.DeliveryTag, multiple: false, requeue: options.RequeueOnFailure);
                }
                catch (Exception ex)
                {
                    // 처리 도중 연결이 끊긴 경우 - ack가 안 갔으니 브로커가 다시 전달한다.
                    _logger.LogWarning(ex, "RabbitMQ ack/nack 실패(연결 끊김 추정) - 재전달될 수 있음: Queue={Queue}",
                        subscription.QueueName);
                }
            };

            channel.BasicConsume(subscription.QueueName, autoAck: false, consumer: consumer);
            subscription.Channel = channel;
            _logger.LogInformation("RabbitMQ 큐 구독 시작: {Queue}", subscription.QueueName);
        }
    }

    private void StopAllSubscriptions()
    {
        Subscription[] snapshot;
        lock (_subscriptionsLock) snapshot = _subscriptions.ToArray();

        foreach (var subscription in snapshot)
        {
            lock (subscription)
            {
                try { subscription.Channel?.Dispose(); } catch { /* 이미 끊김 */ }
                subscription.Channel = null;
            }
        }
    }

    public override void Dispose()
    {
        StopAllSubscriptions();
        try { _connection?.Dispose(); } catch { /* 이미 끊김 */ }
        _outboundSignal.Dispose();
        base.Dispose();
    }

    private enum OutboundKind { Queue, Fanout }

    private sealed record OutboundMessage(OutboundKind Kind, string Target, string Body);

    private sealed class Subscription
    {
        public Subscription(string queueName, Func<BasicDeliverEventArgs, Task<bool>> onMessage, RabbitMqSubscribeOptions options)
        {
            QueueName = queueName;
            OnMessage = onMessage;
            Options = options;
        }

        public string QueueName { get; }
        public Func<BasicDeliverEventArgs, Task<bool>> OnMessage { get; }
        public RabbitMqSubscribeOptions Options { get; }
        public IModel? Channel { get; set; }
    }
}
