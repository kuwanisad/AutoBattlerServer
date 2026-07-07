using System.Threading.Channels;
using Grpc.Core;
using Grpc.Net.Client;
using MasterServer;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MainServer.Common.MasterConnection;

/// <summary>
/// 마스터 서버와의 Connect 양방향 스트림을 평생 유지하려고 시도하는 백그라운드 서비스.
/// 연결이 끊기면 지수 백오프로 재시도한다. 지금은 "마스터에 붙는 것"만 다루고,
/// 실제 매칭/클라이언트 처리 로직은 아직 없다 (TODO로 표시된 자리에 추후 채울 예정).
/// </summary>
public sealed class MasterConnectionService : BackgroundService
{
    private readonly MasterConnectionOptions _options;
    private readonly ILogger<MasterConnectionService> _logger;

    // 마스터로 보낼 메시지의 송신 큐. 하트비트 타이머와, 나중에 추가될 다른 종류의
    // 보고/요청(예: ShutdownNotice)이 둘 다 여기 넣기만 하면 SendLoopAsync 하나가
    // 책임지고 순서대로 WriteAsync한다. 서버 쪽 InstanceInfo.OutboundQueue와 같은 패턴.
    private readonly Channel<MainServerMessage> _outboundQueue =
        Channel.CreateUnbounded<MainServerMessage>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
        });

    public MasterConnectionService(
        IOptions<MasterConnectionOptions> options,
        ILogger<MasterConnectionService> logger)
    {
        _options = options.Value;
        _logger = logger;
    }

    /// <summary>
    /// 마스터로 보낼 메시지를 큐에 넣는다. 연결이 끊긴 상태라도 호출 자체는 안전하다 -
    /// 큐에 쌓여 있다가 재연결되면 그때 나간다.
    /// 매칭 로직 등 다른 클래스에서 이 메서드를 통해 임의의 메시지를 보낼 수 있다.
    /// </summary>
    public void EnqueueMessage(MainServerMessage message) => _outboundQueue.Writer.TryWrite(message);

    /// <summary>
    /// 현재 부하와 수용량을 업데이트하고 마스터에 즉시 상태 보고를 보낸다.
    /// 인게임 서버가 준비 완료되면 Capacity를 실제값으로 올려서 마스터가 배정을 시작하게 한다.
    /// Capacity = 0이면 마스터가 이 서버로 플레이어를 배정하지 않는다.
    /// </summary>
    public void UpdateStatus(int currentLoad, int capacity)
    {
        _currentLoad = currentLoad;
        _capacity = capacity;
        EnqueueMessage(MakeStatus());
    }

    private volatile int _currentLoad;
    private volatile int _capacity;

    private static MainServerMessage MakeHeartbeat() => new()
    {
        Heartbeat = new MainServerHeartbeat(),
    };

    private MainServerMessage MakeStatus() => new()
    {
        Status = new MainServerStatus
        {
            InstanceId = _options.InstanceId,
            ServerType = _options.ServerType,
            Address = _options.SelfAddress,
            CurrentLoad = _currentLoad,
            Capacity = _capacity,
            TimestampUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        },
    };

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var reconnectDelay = _options.ReconnectDelayMin;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunSingleConnectionAsync(stoppingToken);
                reconnectDelay = _options.ReconnectDelayMin;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex, "마스터 접속이 끊겼거나 실패했습니다. {Delay:F0}초 뒤 재시도합니다.",
                    reconnectDelay.TotalSeconds);
            }

            try
            {
                await Task.Delay(reconnectDelay, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            var nextSeconds = Math.Min(reconnectDelay.TotalSeconds * 2, _options.ReconnectDelayMax.TotalSeconds);
            reconnectDelay = TimeSpan.FromSeconds(nextSeconds);
        }
    }

    private async Task RunSingleConnectionAsync(CancellationToken stoppingToken)
    {
        var handler = new SocketsHttpHandler
        {
            EnableMultipleHttp2Connections = true,
            KeepAlivePingDelay = TimeSpan.FromSeconds(10),
            KeepAlivePingTimeout = TimeSpan.FromSeconds(5),
            KeepAlivePingPolicy = HttpKeepAlivePingPolicy.WithActiveRequests,
        };

        using var channel = GrpcChannel.ForAddress(_options.MasterAddress, new GrpcChannelOptions
        {
            HttpHandler = handler,
            DisposeHttpClient = true,
        });

        var client = new MasterControl.MasterControlClient(channel);
        using var call = client.Connect(cancellationToken: stoppingToken);

        _logger.LogInformation(
            "마스터 서버 접속 시도: {Address} (instanceId={InstanceId})",
            _options.MasterAddress, _options.InstanceId);

        var sendTask = SendLoopAsync(call.RequestStream, stoppingToken);
        var receiveTask = ReceiveLoopAsync(call.ResponseStream, stoppingToken);

        var completedTask = await Task.WhenAny(sendTask, receiveTask);
        var remainingTask = completedTask == sendTask ? receiveTask : sendTask;

        _ = remainingTask.ContinueWith(
            t => _logger.LogDebug(t.Exception, "남아있던 스트림 작업이 정리됐습니다."),
            TaskScheduler.Default);

        await completedTask;
    }

    private async Task SendLoopAsync(
        IClientStreamWriter<MainServerMessage> requestStream,
        CancellationToken stoppingToken)
    {
        try
        {
            // 연결 직후 상태 보고를 보내 서버가 이 인스턴스를 즉시 등록할 수 있게 한다.
            await requestStream.WriteAsync(MakeStatus(), stoppingToken);

            var timerTask = Task.Delay(_options.HeartbeatInterval, stoppingToken);
            var queueTask = _outboundQueue.Reader.WaitToReadAsync(stoppingToken).AsTask();

            while (true)
            {
                var completed = await Task.WhenAny(timerTask, queueTask);

                if (completed == timerTask)
                {
                    if (stoppingToken.IsCancellationRequested) break;

                    await requestStream.WriteAsync(MakeHeartbeat(), stoppingToken);
                    _logger.LogInformation("하트비트 송신");

                    timerTask = Task.Delay(_options.HeartbeatInterval, stoppingToken);
                }
                else
                {
                    if (!await queueTask) break;

                    while (_outboundQueue.Reader.TryRead(out var queued))
                    {
                        await requestStream.WriteAsync(queued, stoppingToken);
                    }

                    // 업무 메시지를 보냈으니 타이머를 리셋한다.
                    timerTask = Task.Delay(_options.HeartbeatInterval, stoppingToken);
                    queueTask = _outboundQueue.Reader.WaitToReadAsync(stoppingToken).AsTask();
                }
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
        IAsyncStreamReader<MasterMessage> responseStream,
        CancellationToken stoppingToken)
    {
        var masterHeartbeatTimeout = _options.MasterHeartbeatTimeout;

        try
        {
            while (true)
            {
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                timeoutCts.CancelAfter(masterHeartbeatTimeout);

                bool hasNext;
                try
                {
                    hasNext = await responseStream.MoveNext(timeoutCts.Token);
                }
                catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
                {
                    _logger.LogWarning("마스터 서버 하트비트 타임아웃 - 재연결을 시도합니다.");
                    return;
                }

                if (!hasNext) return;

                var message = responseStream.Current;

                switch (message.PayloadCase)
                {
                    case MasterMessage.PayloadOneofCase.Heartbeat:
                        break;

                    case MasterMessage.PayloadOneofCase.Command:
                        HandleCommand(message.Command);
                        break;

                    default:
                        _logger.LogWarning("알 수 없는 MasterMessage 종류: {Case}", message.PayloadCase);
                        break;
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // 앱 종료에 의한 정상적인 취소
        }
    }

    private void HandleCommand(MasterCommand command)
    {
        switch (command.CommandCase)
        {
            case MasterCommand.CommandOneofCase.Broadcast:
                _logger.LogInformation("운영 공지 수신: {Message}", command.Broadcast.Message);
                // TODO: 인게임 서버로 릴레이, 접속 중인 클라이언트들에게 전달
                break;

            case MasterCommand.CommandOneofCase.Drain:
                _logger.LogInformation("드레인 신호 수신: 신규 수용 중단={Stop}", command.Drain.StopAcceptingNew);
                // TODO: 신규 매칭 접수 중단
                break;

            default:
                _logger.LogWarning("알 수 없는 MasterCommand 종류: {Case}", command.CommandCase);
                break;
        }
    }
}
