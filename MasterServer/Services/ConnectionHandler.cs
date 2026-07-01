using System.Threading.Channels;
using Grpc.Core;
using MasterServer.Registry;

namespace MasterServer.Services;

/// <summary>
/// Connect RPC의 실제 로직 담당.
/// 메인 서버 하나가 접속을 유지하는 동안 열려있는 양방향 스트림을 관리한다:
///   - ReceiveLoopAsync: 메인 서버 → 마스터 방향 (하트비트 수신, 레지스트리 등록)
///   - SendLoopAsync: 마스터 → 메인 서버 방향 (자체 하트비트 + OutboundQueue 소비)
/// </summary>
internal sealed class ConnectionHandler
{
    private readonly InstanceRegistry _registry;
    private readonly ILogger _logger;

    internal ConnectionHandler(InstanceRegistry registry, ILogger logger)
    {
        _registry = registry;
        _logger = logger;
    }

    internal async Task HandleAsync(
        IAsyncStreamReader<MainServerMessage> requestStream,
        IServerStreamWriter<MasterMessage> responseStream,
        CancellationToken cancellationToken)
    {
        var connectionToken = Guid.NewGuid();

        // InstanceId는 첫 MainServerMessage가 와야 알 수 있지만, OutboundQueue는 그 전에도
        // 필요하다(SendLoop가 즉시 하트비트를 시작해야 하므로). 그래서 이 연결 전용
        // InstanceInfo를 미리 만들어 양쪽 루프에 직접 전달한다.
        var pendingInfo = new InstanceInfo
        {
            InstanceId = string.Empty,
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
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // 앱 종료에 의한 정상적인 취소 - 예외를 삼키고 정리 단계로 넘어간다.
        }
        finally
        {
            if (!string.IsNullOrEmpty(pendingInfo.InstanceId))
            {
                _registry.RemoveIfCurrent(pendingInfo.InstanceId, connectionToken);
                _logger.LogWarning("인스턴스 접속 종료 처리: {InstanceId}", pendingInfo.InstanceId);
            }
            else
            {
                // 첫 MainServerMessage가 오기 전에 연결이 끊긴 경우.
                // 레지스트리에 등록된 것이 없으니 정리할 건 없지만, 디버깅을 위해 기록해둔다.
                _logger.LogWarning("인스턴스 식별 전 연결 종료 (connectionToken={Token})", connectionToken);
            }
        }
    }

    /// <summary>
    /// 메인 서버 → 마스터 방향. 마지막 메시지로부터 HeartbeatTimeout이 지나면
    /// 죽은 것으로 간주하는 슬라이딩 윈도우를 구현한다.
    /// </summary>
    private async Task ReceiveLoopAsync(
        IAsyncStreamReader<MainServerMessage> requestStream,
        InstanceInfo pendingInfo,
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
                    // 앱 종료가 아니라 우리가 건 하트비트 타임아웃이 발동한 경우.
                    _logger.LogWarning(
                        "하트비트 타임아웃으로 접속 종료 처리: {InstanceId}",
                        string.IsNullOrEmpty(pendingInfo.InstanceId) ? "(식별 전)" : pendingInfo.InstanceId);
                    return;
                }

                if (!hasNext)
                {
                    return; // 메인 서버가 스트림을 정상적으로 닫음
                }

                var message = requestStream.Current;

                switch (message.PayloadCase)
                {
                    case MainServerMessage.PayloadOneofCase.Heartbeat:
                        // 빈 생존 신호 - 수신 자체로 타임아웃이 리셋된다.
                        break;

                    case MainServerMessage.PayloadOneofCase.Status:
                        HandleStatus(message.Status, pendingInfo);
                        break;

                    case MainServerMessage.PayloadOneofCase.None:
                        // 예상치 못한 빈 메시지 - 무시
                        break;

                    default:
                        _logger.LogWarning("알 수 없는 MainServerMessage 종류: {Case}", message.PayloadCase);
                        break;
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // 앱 종료에 의한 정상적인 취소 - 루프를 정상 종료한다.
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

    /// <summary>
    /// 마스터 → 메인 서버 방향. idle heartbeat 패턴을 사용한다:
    /// - 큐에 업무 메시지가 있으면 그걸 보내고 타이머를 리셋한다.
    /// - KeepaliveInterval 동안 보낼 게 없으면 그때만 빈 커맨드를 keepalive로 보낸다.
    /// 실제 WriteAsync 호출은 이 루프 하나만 하므로 동시 쓰기 문제가 없다.
    /// </summary>
    private async Task SendLoopAsync(
        IServerStreamWriter<MasterMessage> responseStream,
        InstanceInfo pendingInfo,
        CancellationToken cancellationToken)
    {
        try
        {
            var queueReader = pendingInfo.OutboundQueue.Reader;

            // 연결 직후 첫 keepalive를 즉시 보낸다. PeriodicTimer는 첫 tick도
            // interval만큼 기다리기 때문에, 타이머만 믿으면 연결 후 침묵 구간이 생긴다.
            await responseStream.WriteAsync(MakeHeartbeat(), cancellationToken);

            using var keepaliveTimer = new PeriodicTimer(ConnectionConstants.KeepaliveInterval);
            var timerTask = keepaliveTimer.WaitForNextTickAsync(cancellationToken).AsTask();
            var queueTask = queueReader.WaitToReadAsync(cancellationToken).AsTask();

            while (true)
            {
                var completed = await Task.WhenAny(timerTask, queueTask);

                if (completed == timerTask)
                {
                    if (!await timerTask) return;

                    // KeepaliveInterval 동안 보낼 업무 메시지가 없었던 경우에만 keepalive 전송
                    await responseStream.WriteAsync(MakeHeartbeat(), cancellationToken);

                    timerTask = keepaliveTimer.WaitForNextTickAsync(cancellationToken).AsTask();
                }
                else
                {
                    if (!await queueTask) return; // 큐 닫힘 = 연결 정리 대상

                    while (queueReader.TryRead(out var queued))
                    {
                        await responseStream.WriteAsync(queued, cancellationToken);
                    }

                    // 업무 메시지를 보냈으니 타이머를 리셋한다.
                    // PeriodicTimer.Period를 같은 값으로 재설정하면 내부 카운터가 리셋된다.
                    keepaliveTimer.Period = ConnectionConstants.KeepaliveInterval;
                    timerTask = keepaliveTimer.WaitForNextTickAsync(cancellationToken).AsTask();

                    queueTask = queueReader.WaitToReadAsync(cancellationToken).AsTask();
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // 앱 종료에 의한 정상적인 취소
        }
    }

    private static MasterMessage MakeHeartbeat() => new()
    {
        Heartbeat = new MasterHeartbeat(),
    };
}
