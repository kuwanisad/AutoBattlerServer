using RabbitMQ.Client.Events;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Shared.Common.Account;
using SubServer.Common.Messaging;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DummySubServer;

/// <summary>
/// reward-queue 메시지 본문은 JSON으로 가정한다(더미/예시라 실제 스키마는 상위 시스템이
/// 뭘 발행하느냐에 따라 달라질 것): { "playerId", "sourceEventId", "mailType",
/// "displayContent", "payloadJson" }. mailType은 MailType enum의 C# 멤버 이름
/// 문자열(PascalCase, 예: "ItemProvision" - proto 원본 이름 "ITEM_PROVISION"이
/// 아님)로 받는다 - JsonStringEnumConverter가 파싱을 처리한다. sourceEventId는 이
/// 메시지 자체의 고유 ID로 채워 넣어야 한다 - RabbitMQ가 "최소 한 번" 배달이라 같은
/// 메시지가 재전달돼도 GrantMail이 멱등하게 처리한다.
/// </summary>
internal sealed record RewardEvent(
    string PlayerId,
    string SourceEventId,
    GameServer.Common.MailType MailType,
    string DisplayContent,
    string PayloadJson);

public sealed class DummyRabbitMqConsumer : RabbitMqConsumerBase
{
    private readonly RemoteMailNotifier _mailNotifier;
    private readonly ILogger<DummyRabbitMqConsumer> _logger;

    public DummyRabbitMqConsumer(
        IOptions<RabbitMqConsumerOptions> options,
        RemoteMailNotifier mailNotifier,
        ILogger<DummyRabbitMqConsumer> logger)
        : base(options, logger)
    {
        _mailNotifier = mailNotifier;
        _logger = logger;
    }

    protected override async Task HandleMessageAsync(
        BasicDeliverEventArgs ea,
        CancellationToken cancellationToken)
    {
        var body = GetBody(ea);
        _logger.LogInformation(
            "[RabbitMQ] 메시지 수신 - Queue={Queue} DeliveryTag={Tag} Body={Body}",
            ea.RoutingKey, ea.DeliveryTag, body);

        RewardEvent? evt;
        try
        {
            var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            options.Converters.Add(new JsonStringEnumConverter());
            evt = JsonSerializer.Deserialize<RewardEvent>(body, options);
        }
        catch (JsonException ex)
        {
            // 파싱 자체가 불가능한 메시지 - 재큐잉해봤자 계속 실패할 게 뻔하므로(영구적
            // 실패) 예외를 던지지 않고 그냥 버린다(RabbitMqConsumerBase가 이 메서드에서
            // 예외가 안 나면 성공으로 보고 ack한다).
            _logger.LogError(ex, "reward-queue 메시지 파싱 실패 - 버림: {Body}", body);
            return;
        }

        if (evt == null || string.IsNullOrEmpty(evt.PlayerId) || string.IsNullOrEmpty(evt.SourceEventId))
        {
            _logger.LogError("reward-queue 메시지 필드 누락 - 버림: {Body}", body);
            return;
        }

        // GrantAndNotifyRemoteAsync가 던지는 예외는 여기서 안 삼킨다 - RabbitMqConsumerBase가
        // 이걸 보고 Nack(재큐잉)하도록 그대로 위로 전파한다.
        await _mailNotifier.GrantAndNotifyRemoteAsync(
            evt.PlayerId, evt.SourceEventId, evt.MailType, evt.DisplayContent, evt.PayloadJson);
    }
}
