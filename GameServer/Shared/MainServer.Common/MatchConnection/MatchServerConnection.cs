using Grpc.Core;
using Grpc.Net.Client;
using GameServer.Common;
using MainServer.Common.MasterConnection;
using MatchingServer;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Shared.Common;

namespace MainServer.Common.MatchConnection;

/// <summary>
/// 매칭 서버와의 MatchConnectionControl.Connect 양방향 스트림을 평생 유지하려고 시도한다.
/// MasterConnectionService ↔ MasterServer.ConnectionHandler와 완전히 같은 패턴이다.
///
/// 이 접속이 로비/게임 에이전트의 "등록"을 대체한다 - 예전 RegisterServer/UnregisterServer
/// 단항 호출 + Redis TTL 폴링과 달리, 스트림이 끊기는 순간 매칭 서버가 즉시 서버 풀에서
/// 제거한다(MatchConnectionHandler 참고).
///
/// 로비는 매칭 서버로부터 MatchNotification(매칭 확정/취소)을 이 스트림으로 받는다 -
/// IMatchNotificationHandler가 등록돼 있으면 그쪽으로 전달한다.
/// </summary>
public sealed class MatchServerConnection : BackgroundService
{
    private readonly MasterConnectionOptions _options;
    private readonly MasterConnectionService _masterConnection;
    private readonly IMatchNotificationHandler? _notificationHandler;
    private readonly ILogger<MatchServerConnection> _logger;

    // 매칭 서버 접속을 시작해도 되는 시점을 나타내는 게이트.
    // 로비는 즉시 준비된 것으로 본다. 게임 에이전트는 실제 게임 서버(Unity)가 Ready를
    // 보내기 전까지는 매칭 후보가 되면 안 되므로, GameServerHandler가 SignalReady()를
    // 호출하기 전까지 접속을 미룬다.
    private readonly TaskCompletionSource _readyGate =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public MatchServerConnection(
        IOptions<MasterConnectionOptions> options,
        MasterConnectionService masterConnection,
        ILogger<MatchServerConnection> logger,
        IMatchNotificationHandler? notificationHandler = null)
    {
        _options = options.Value;
        _masterConnection = masterConnection;
        _notificationHandler = notificationHandler;
        _logger = logger;
    }

    public void SignalReady() => _readyGate.TrySetResult();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (_options.ServerType == ServerType.Lobby)
        {
            SignalReady();
        }

        try
        {
            await _readyGate.Task.WaitAsync(stoppingToken);
        }
        catch (OperationCanceledException)
        {
            return; // Ready 신호를 못 받고 앱이 종료된 경우
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunSingleConnectionAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "매칭 서버 접속이 끊겼거나 실패했습니다. {Delay:F0}초 뒤 재시도합니다.",
                    ConnectionConstants.ReconnectDelay.TotalSeconds);
            }

            try
            {
                await Task.Delay(ConnectionConstants.ReconnectDelay, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task RunSingleConnectionAsync(CancellationToken stoppingToken)
    {
        // 마스터로부터 매칭 서버 주소를 아직 못 받았으면 대기 (폴링 - 이벤트가 따로 없어서).
        while (string.IsNullOrEmpty(_masterConnection.MatchingServerAddress))
        {
            await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
        }

        var address = _masterConnection.MatchingServerAddress;

        using var channel = GrpcChannel.ForAddress(address);
        var client = new MatchConnectionControl.MatchConnectionControlClient(channel);
        using var call = client.Connect(cancellationToken: stoppingToken);

        _logger.LogInformation("매칭 서버 접속 시도: {Address} (serverId={ServerId})",
            address, _options.InstanceId);

        var sendTask = SendLoopAsync(call.RequestStream, stoppingToken);
        var receiveTask = ReceiveLoopAsync(call.ResponseStream, stoppingToken);

        var completedTask = await Task.WhenAny(sendTask, receiveTask);
        var remainingTask = completedTask == sendTask ? receiveTask : sendTask;

        _ = remainingTask.ContinueWith(
            t => _logger.LogDebug(t.Exception, "남아있던 매칭 서버 스트림 작업이 정리됐습니다."),
            TaskScheduler.Default);

        await completedTask;
    }

    private async Task SendLoopAsync(
        IClientStreamWriter<ServerToMatchMessage> requestStream,
        CancellationToken stoppingToken)
    {
        try
        {
            // 연결 직후 상태 보고를 보내 매칭 서버가 이 인스턴스를 즉시 등록할 수 있게 한다.
            await requestStream.WriteAsync(new ServerToMatchMessage
            {
                Status = new MatchConnectionStatus
                {
                    ServerId = _options.InstanceId,
                    ServerType = _options.ServerType,
                    Address = _options.SelfAddress,
                },
            }, stoppingToken);

            while (true)
            {
                await Task.Delay(ConnectionConstants.KeepaliveInterval, stoppingToken);
                await requestStream.WriteAsync(new ServerToMatchMessage
                {
                    Heartbeat = new MatchConnectionHeartbeat(),
                }, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // 앱 종료에 의한 정상적인 취소
        }
        finally
        {
            await requestStream.CompleteAsync();
        }
    }

    private async Task ReceiveLoopAsync(
        IAsyncStreamReader<MatchToServerMessage> responseStream,
        CancellationToken stoppingToken)
    {
        try
        {
            while (true)
            {
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                timeoutCts.CancelAfter(ConnectionConstants.HeartbeatTimeout);

                bool hasNext;
                try
                {
                    hasNext = await responseStream.MoveNext(timeoutCts.Token);
                }
                catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
                {
                    _logger.LogWarning("매칭 서버 하트비트 타임아웃 - 재연결을 시도합니다.");
                    return;
                }

                if (!hasNext) return;

                var message = responseStream.Current;

                switch (message.PayloadCase)
                {
                    case MatchToServerMessage.PayloadOneofCase.Heartbeat:
                        break;

                    case MatchToServerMessage.PayloadOneofCase.MatchNotification:
                        _notificationHandler?.HandleMatchNotification(message.MatchNotification);
                        break;

                    default:
                        _logger.LogWarning("알 수 없는 MatchToServerMessage 종류: {Case}", message.PayloadCase);
                        break;
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // 앱 종료에 의한 정상적인 취소
        }
    }
}
