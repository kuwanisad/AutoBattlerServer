using RabbitMQ.Client.Events;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Shared.Common.Messaging;
using System.Text;

namespace SubServer.Common.Messaging;

/// <summary>
/// RabbitMQ 컨슈머 베이스 클래스. 하위 서버가 이를 상속해서 HandleMessageAsync만 구현하면
/// 큐 선언/구독/ack가 공통으로 처리된다.
///
/// 접속/재접속은 RabbitMqBroker(Shared.Common - 메인 서버와 공통)가 맡는다 - 예전엔 이 클래스가
/// 직접 접속하고 5초마다 재시도했지만, 이제 모든 서버가 같은 중개 클래스로 접속한다. 이 클래스는
/// 시작할 때 구독을 등록만 하고, 실제 구독은 RabbitMqBroker가 접속/재접속될 때마다 연다.
/// 하위 서버가 RabbitMQ로 발행해야 할 때도 RabbitMqBroker를 주입받아 PublishToQueue를 쓰면 된다
/// (연결이 없으면 쌓아뒀다가 접속되면 보냄 - 별도 프로듀서 베이스가 필요 없다).
///
/// at-least-once 보장: HandleMessageAsync 성공 시 Ack, 실패(예외) 시 Nack(재큐잉 안 함 - 예전 동작 유지).
/// HandleMessageAsync는 반드시 멱등성을 보장해야 한다 (인박스 패턴 권장).
/// </summary>
public abstract class RabbitMqConsumerBase : BackgroundService
{
    private readonly RabbitMqConsumerOptions _options;
    private readonly RabbitMqBroker _broker;
    protected readonly ILogger Logger;

    protected RabbitMqConsumerBase(
        IOptions<RabbitMqConsumerOptions> options,
        RabbitMqBroker broker,
        ILogger logger)
    {
        _options = options.Value;
        _broker = broker;
        Logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _broker.SubscribeRaw(_options.QueueName, async ea =>
        {
            await HandleMessageAsync(ea, stoppingToken);
            return true;
        }, new RabbitMqSubscribeOptions
        {
            PrefetchCount = _options.PrefetchCount,
            RequeueOnFailure = false,
            FanoutExchange = _options.ExchangeName,
        });

        // 구독은 RabbitMqBroker가 유지한다 - 이 서비스는 서버가 끝날 때까지 살아만 있으면 된다.
        try { await Task.Delay(Timeout.Infinite, stoppingToken); }
        catch (OperationCanceledException) { }
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
