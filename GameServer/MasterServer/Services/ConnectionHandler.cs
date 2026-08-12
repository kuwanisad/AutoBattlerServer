using Grpc.Core;
using MasterServer.Registry;
using Shared.Common;
using GameServer.Common;

namespace MasterServer.Services;

/// <summary>
/// Connect RPC의 실제 로직 담당.
/// ShutdownNotice 수신 여부로 정상/비정상 종료를 구분한다.
/// 비정상 종료 시 CleanupServerConnection을 통해 RegistryCleanupServer에 통보한다.
/// </summary>
internal sealed class ConnectionHandler
{
    private readonly InstanceRegistry _registry;
    private readonly CleanupServerConnection _cleanupConnection;
    private readonly ILogger _logger;

    private sealed class ShutdownFlag
    {
        public bool IsNormal { get; set; }
    }

    internal ConnectionHandler(
        InstanceRegistry registry,
        CleanupServerConnection cleanupConnection,
        ILogger logger)
    {
        _registry = registry;
        _cleanupConnection = cleanupConnection;
        _logger = logger;
    }

    internal async Task HandleAsync(
        IAsyncStreamReader<MainServerMessage> requestStream,
        IServerStreamWriter<MasterMessage> responseStream,
        CancellationToken cancellationToken)
    {
        var connectionToken = Guid.NewGuid();

        var pendingInfo = new InstanceInfo
        {
            InstanceId = string.Empty,
            ServerType = ServerType.Unspecified,
            Address = string.Empty,
            ConnectionToken = connectionToken,
        };

        var shutdownFlag = new ShutdownFlag();
        var receiveTask = ReceiveLoopAsync(requestStream, pendingInfo, cancellationToken, shutdownFlag);
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
            if (!string.IsNullOrEmpty(pendingInfo.InstanceId))
            {
                _registry.RemoveIfCurrent(pendingInfo.InstanceId, connectionToken);
                _logger.LogWarning("인스턴스 접속 종료 처리: {InstanceId}", pendingInfo.InstanceId);

                // 정상 종료(ShutdownNotice 수신)가 아닌 경우에만 RegistryCleanupServer에 통보
                if (!shutdownFlag.IsNormal)
                    _cleanupConnection.NotifyServerDied(pendingInfo.InstanceId);
            }
            else
            {
                _logger.LogWarning("인스턴스 식별 전 연결 종료 (connectionToken={Token})", connectionToken);
            }
        }
    }

    private async Task ReceiveLoopAsync(
        IAsyncStreamReader<MainServerMessage> requestStream,
        InstanceInfo pendingInfo,
        CancellationToken cancellationToken,
        ShutdownFlag shutdownFlag)
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
                    _logger.LogWarning(
                        "하트비트 타임아웃으로 접속 종료 처리: {InstanceId}",
                        string.IsNullOrEmpty(pendingInfo.InstanceId) ? "(식별 전)" : pendingInfo.InstanceId);
                    return;
                }

                if (!hasNext) return;

                var message = requestStream.Current;

                switch (message.PayloadCase)
                {
                    case MainServerMessage.PayloadOneofCase.Heartbeat:
                        break;

                    case MainServerMessage.PayloadOneofCase.Status:
                        HandleStatus(message.Status, pendingInfo);
                        break;

                    case MainServerMessage.PayloadOneofCase.Shutdown:
                        _logger.LogInformation("정상 종료 신호 수신: {InstanceId}", pendingInfo.InstanceId);
                        shutdownFlag.IsNormal = true;
                        return;

                    case MainServerMessage.PayloadOneofCase.None:
                        break;

                    default:
                        _logger.LogWarning("알 수 없는 MainServerMessage 종류: {Case}", message.PayloadCase);
                        break;
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            shutdownFlag.IsNormal = true; // 앱 종료는 정상 종료로 취급
        }
    }

    private void HandleStatus(MainServerStatus status, InstanceInfo pendingInfo)
    {
        pendingInfo.InstanceId = status.InstanceId;
        pendingInfo.ServerType = status.ServerType;
        pendingInfo.Address = status.Address;
        pendingInfo.CurrentLoad = status.CurrentLoad;
        pendingInfo.Capacity = status.Capacity;

        _registry.Upsert(pendingInfo);

        _logger.LogInformation(
            "상태 수신: {InstanceId} ({ServerType}) 부하 {Load}/{Capacity}",
            status.InstanceId, status.ServerType, status.CurrentLoad, status.Capacity);
    }

    private async Task SendLoopAsync(
        IServerStreamWriter<MasterMessage> responseStream,
        InstanceInfo pendingInfo,
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

    private static MasterMessage MakeHeartbeat() => new()
    {
        Heartbeat = new MasterHeartbeat(),
    };
}
