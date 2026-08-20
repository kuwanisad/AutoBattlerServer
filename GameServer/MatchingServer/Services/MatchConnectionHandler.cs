using Grpc.Core;
using MatchingServer.Registry;
using Shared.Common;
using GameServer.Common;

namespace MatchingServer.Services;

/// <summary>
/// Connect RPC의 실제 로직 담당. 로비/게임 에이전트가 이 스트림을 여는 게 곧 등록이고,
/// 스트림이 끊기는 게 곧 해제다 - 예전 RegisterServer/UnregisterServer 단항 호출과
/// Redis TTL 폴링에 기대지 않고, 접속 종료를 즉시 감지한다.
/// MasterServer.Services.ConnectionHandler와 같은 패턴 (인증만 없음 - 매칭 서버는 아직
/// 마스터처럼 IServerAuthenticator 인프라가 없어서 생략, 필요해지면 나중에 추가).
/// </summary>
internal sealed class MatchConnectionHandler
{
    private readonly ConnectedServerRegistry _registry;
    private readonly MatchEngine _matchEngine;
    private readonly ILogger _logger;

    internal MatchConnectionHandler(
        ConnectedServerRegistry registry,
        MatchEngine matchEngine,
        ILogger logger)
    {
        _registry = registry;
        _matchEngine = matchEngine;
        _logger = logger;
    }

    internal async Task HandleAsync(
        IAsyncStreamReader<ServerToMatchMessage> requestStream,
        IServerStreamWriter<MatchToServerMessage> responseStream,
        ServerCallContext context)
    {
        var cancellationToken = context.CancellationToken;
        var connectionToken = Guid.NewGuid();

        var pendingInfo = new ConnectedServerInfo
        {
            ServerId = string.Empty,
            ServerType = ServerType.Unspecified,
            Address = string.Empty,
            ConnectionToken = connectionToken,
        };

        var receiveTask = ReceiveLoopAsync(requestStream, pendingInfo, cancellationToken);
        var sendTask = SendLoopAsync(responseStream, pendingInfo, cancellationToken);

        var completedTask = await Task.WhenAny(receiveTask, sendTask);
        var remainingTask = completedTask == receiveTask ? sendTask : receiveTask;

        _ = remainingTask.ContinueWith(
            t => _logger.LogDebug(t.Exception, "남아있던 Connect 루프가 정리됐습니다."),
            TaskScheduler.Default);

        try
        {
            await completedTask;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        finally
        {
            if (!string.IsNullOrEmpty(pendingInfo.ServerId))
            {
                _registry.RemoveIfCurrent(pendingInfo.ServerId, connectionToken);
                _logger.LogWarning("서버 접속 종료: {ServerId} ({ServerType})",
                    pendingInfo.ServerId, pendingInfo.ServerType);

                // 이 서버가 관여하던 매치/대기열을 MatchEngine한테 정리시킨다.
                // (감지는 여기서, "뭘 해야 하는지" 판단은 MatchEngine 안에서.)
                _matchEngine.HandleServerDisconnected(pendingInfo.ServerId, pendingInfo.ServerType);
            }
            else
            {
                _logger.LogWarning("서버 식별 전 연결 종료 (connectionToken={Token})", connectionToken);
            }
        }
    }

    private async Task ReceiveLoopAsync(
        IAsyncStreamReader<ServerToMatchMessage> requestStream,
        ConnectedServerInfo pendingInfo,
        CancellationToken cancellationToken)
    {
        try
        {
            while (true)
            {
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeoutCts.CancelAfter(ConnectionConstants.HeartbeatTimeout);

                bool hasNext;
                try
                {
                    hasNext = await requestStream.MoveNext(timeoutCts.Token);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    _logger.LogWarning("하트비트 타임아웃으로 접속 종료 처리: {ServerId}",
                        string.IsNullOrEmpty(pendingInfo.ServerId) ? "(식별 전)" : pendingInfo.ServerId);
                    return;
                }

                if (!hasNext) return;

                var message = requestStream.Current;

                switch (message.PayloadCase)
                {
                    case ServerToMatchMessage.PayloadOneofCase.Heartbeat:
                        break;

                    case ServerToMatchMessage.PayloadOneofCase.Status:
                        HandleStatus(message.Status, pendingInfo);
                        break;

                    case ServerToMatchMessage.PayloadOneofCase.None:
                        break;

                    default:
                        _logger.LogWarning("알 수 없는 ServerToMatchMessage 종류: {Case}", message.PayloadCase);
                        break;
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
    }

    private void HandleStatus(MatchConnectionStatus status, ConnectedServerInfo pendingInfo)
    {
        pendingInfo.ServerId = status.ServerId;
        pendingInfo.ServerType = status.ServerType;
        pendingInfo.Address = status.Address;

        _registry.Upsert(pendingInfo);

        _logger.LogInformation("서버 접속: {ServerId} ({ServerType}) @ {Address}",
            status.ServerId, status.ServerType, status.Address);
    }

    private async Task SendLoopAsync(
        IServerStreamWriter<MatchToServerMessage> responseStream,
        ConnectedServerInfo pendingInfo,
        CancellationToken cancellationToken)
    {
        try
        {
            var queueReader = pendingInfo.OutboundQueue.Reader;

            await responseStream.WriteAsync(MakeHeartbeat(), cancellationToken);

            var timerTask = Task.Delay(ConnectionConstants.KeepaliveInterval, cancellationToken);
            var queueTask = queueReader.WaitToReadAsync(cancellationToken).AsTask();

            while (true)
            {
                var completed = await Task.WhenAny(timerTask, queueTask);

                if (completed == timerTask)
                {
                    if (cancellationToken.IsCancellationRequested) return;

                    await responseStream.WriteAsync(MakeHeartbeat(), cancellationToken);
                    timerTask = Task.Delay(ConnectionConstants.KeepaliveInterval, cancellationToken);
                }
                else
                {
                    if (!await queueTask) return;

                    while (queueReader.TryRead(out var queued))
                        await responseStream.WriteAsync(queued, cancellationToken);

                    timerTask = Task.Delay(ConnectionConstants.KeepaliveInterval, cancellationToken);
                    queueTask = queueReader.WaitToReadAsync(cancellationToken).AsTask();
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
    }

    private static MatchToServerMessage MakeHeartbeat() => new()
    {
        Heartbeat = new MatchConnectionHeartbeat(),
    };
}