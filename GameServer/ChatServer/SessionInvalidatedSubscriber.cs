using System.Text.Json;
using MainServer.Common.Messaging;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Shared.Common.Messaging;

namespace ChatServer;

/// <summary>
/// 로비/게임 에이전트가 세션을 지울 때마다(로그아웃, 재접속 유예 시간 초과 등) 발행하는
/// "session-invalidated" 큐를 구독해서, 해당 playerId의 웹소켓 연결에 강제 종료를
/// 요청한다. 실제 종료는 그 연결을 소유한 WebSocketHandler.ReceiveLoopAsync가 취소
/// 신호를 받아 스스로 수행한다 - 이 클래스는 WebSocket을 직접 건드리지 않는다.
///
/// 채팅 서버는 "이 클라를 계속 서비스해도 되는지" 스스로 판단하지 않는다 - 그 판단은
/// 전적으로 메인 서버 몫이고, 채팅 서버는 이 신호만 보고 따른다. 재연결 시 토큰을
/// 채팅 서버가 직접 회전시키지 않는 것도 같은 원칙 - 채팅 서버는 세션 레지스트리를
/// 읽기만 해서 "지금 유효한가"만 확인하고, 그 이상의 판단(회전, 강제 종료 여부)은 안 한다.
/// </summary>
public sealed class SessionInvalidatedSubscriber : IHostedService
{
    private readonly RabbitMqConsumer _consumer;
    private readonly ChatConnectionRegistry _connections;
    private readonly ILogger<SessionInvalidatedSubscriber> _logger;

    public SessionInvalidatedSubscriber(
        RabbitMqConsumer consumer,
        ChatConnectionRegistry connections,
        ILogger<SessionInvalidatedSubscriber> logger)
    {
        _consumer = consumer;
        _connections = connections;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _consumer.Subscribe(SessionInvalidatedEvent.QueueName, HandleAsync);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private Task<bool> HandleAsync(string json)
    {
        SessionInvalidatedEvent? evt;
        try
        {
            evt = JsonSerializer.Deserialize<SessionInvalidatedEvent>(json);
        }
        catch (JsonException ex)
        {
            _logger.LogError(ex, "SessionInvalidated 이벤트 파싱 실패 - 버림: {Json}", json);
            return Task.FromResult(true); // 영구적 실패 - 재큐잉해도 계속 실패하므로 ack시키고 버린다
        }

        if (evt == null || string.IsNullOrEmpty(evt.PlayerId))
        {
            _logger.LogError("SessionInvalidated 이벤트 필드 누락 - 버림: {Json}", json);
            return Task.FromResult(true);
        }

        var connection = _connections.TryGet(evt.PlayerId);
        if (connection == null)
        {
            // 이 플레이어가 지금 채팅 서버에 접속 중이 아님 - 정상적인 경우(애초에 안
            // 켰거나 이미 끊긴 상태)라 조용히 끝낸다.
            return Task.FromResult(true);
        }

        // WebSocket을 여기서 직접 건드리지 않는다 - 그 WebSocket의 유일한 조작자는
        // 그 연결을 소유한 WebSocketHandler.ReceiveLoopAsync 태스크뿐이어야 한다(안
        // 그러면 소유 태스크가 이미 걸어둔 ReceiveAsync와 여기서의 직접 조작이 충돌해
        // 영원히 멈추는 버그가 난다 - 실제로 겪었음, KNOWN_ISSUES.md 참고). 대신
        // 취소 토큰만 신호를 보내고, 실제 종료는 소유 태스크가 스스로 수행한다.
        connection.ForceCloseReason = "세션 종료";
        connection.Cts.Cancel();
        _logger.LogInformation("SessionInvalidated로 채팅 연결 강제 종료 요청: {PlayerId}", evt.PlayerId);

        return Task.FromResult(true);
    }
}