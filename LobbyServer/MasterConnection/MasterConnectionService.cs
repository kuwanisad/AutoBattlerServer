using System.Threading.Channels;
using Grpc.Core;
using Grpc.Net.Client;
using MasterServer;
using Microsoft.Extensions.Options;

namespace LobbyServer.MasterConnection;

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
    /// 큐에 쌓여 있다가 재연결되면 그때 나간다 (단, 재연결 시 channel을 새로 만들지 않고
    /// 이 인스턴스 필드를 그대로 재사용하므로 큐 내용이 연결 너머로 유지된다).
    /// 매칭 로직 등 다른 클래스에서 이 메서드를 통해 임의의 메시지를 보낼 수 있다.
    /// </summary>
    public void EnqueueMessage(MainServerMessage message) => _outboundQueue.Writer.TryWrite(message);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var reconnectDelay = _options.ReconnectDelayMin;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunSingleConnectionAsync(stoppingToken);
                reconnectDelay = _options.ReconnectDelayMin; // 한 번이라도 정상 접속했다면 백오프 초기화
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break; // 앱 종료에 의한 정상적인 취소
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

    /// <summary>접속 한 번의 생애주기. 정상적으로 끝나는 경우는 없고, 항상 예외나 취소로 끝난다.</summary>
    private async Task RunSingleConnectionAsync(CancellationToken stoppingToken)
    {
        // 마스터 쪽에 HTTP/2 PING keepalive를 걸어둔 것과 대응되는 클라이언트 측 설정.
        // 애플리케이션 메시지가 한동안 없어도 전송 계층에서 생사를 확인한다.
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

        // 둘 중 하나라도 먼저 끝나면(보통 연결이 죽었다는 뜻) 이 메서드를 빠져나간다.
        // 그러면 위 using들이 call/channel을 정리하면서, 아직 안 끝난 나머지 한쪽의
        // 읽기/쓰기도 취소시켜 마무리하게 만든다.
        var completedTask = await Task.WhenAny(sendTask, receiveTask);
        var remainingTask = completedTask == sendTask ? receiveTask : sendTask;

        // 나머지 한쪽의 예외는 별도로 로깅만 하고 삼킨다 - 보통 같은 원인(연결 끊김)이라
        // 먼저 끝난 쪽의 예외만 바깥 루프에 전파하면 충분하다.
        _ = remainingTask.ContinueWith(
            t => _logger.LogDebug(t.Exception, "남아있던 스트림 작업이 정리됐습니다."),
            TaskScheduler.Default);

        await completedTask; // 여기서 던지는 예외가 바깥 ExecuteAsync의 catch로 전파되어 재연결을 트리거한다.
    }

    private static MainServerMessage MakeHeartbeat() => new()
    {
        Heartbeat = new MainServerHeartbeat(),
    };

    private MainServerMessage MakeStatus() => new()
    {
        Status = new MainServerStatus
        {
            InstanceId = _options.InstanceId,
            ServerType = ServerType.Lobby,
            Address = _options.SelfAddress,
            CurrentLoad = 0,    // TODO: 실제 매칭 대기열 인원수로 교체
            Capacity = 100,     // TODO: 실제 수용 가능 인원으로 교체
            TimestampUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        },
    };

    /// <summary>
    /// 마스터로 보내는 송신 루프. idle heartbeat 패턴:
    /// - 타이머 만료 시 상태 보고(MakeStatus)를 보내고 타이머를 리셋한다.
    /// - 큐에 업무 메시지가 있으면 그걸 보내고 타이머를 리셋한다.
    /// 어떤 메시지든 전송 자체가 서버에게 "살아있다"는 신호가 된다.
    /// </summary>
    private async Task SendLoopAsync(
        IClientStreamWriter<MainServerMessage> requestStream,
        CancellationToken stoppingToken)
    {
        try
        {
            // 연결 직후 상태 보고를 보내 서버가 이 인스턴스를 즉시 등록할 수 있게 한다.
            // 어떤 메시지든 수신 자체가 생존 신호이므로 별도 하트비트는 필요 없다.
            await requestStream.WriteAsync(MakeStatus(), stoppingToken);

            using var heartbeatTimer = new PeriodicTimer(_options.HeartbeatInterval);
            var timerTask = heartbeatTimer.WaitForNextTickAsync(stoppingToken).AsTask();
            var queueTask = _outboundQueue.Reader.WaitToReadAsync(stoppingToken).AsTask();

            while (true)
            {
                var completed = await Task.WhenAny(timerTask, queueTask);

                if (completed == timerTask)
                {
                    if (!await timerTask) break;

                    await requestStream.WriteAsync(MakeHeartbeat(), stoppingToken);
                    _logger.LogInformation("하트비트 송신");

                    timerTask = heartbeatTimer.WaitForNextTickAsync(stoppingToken).AsTask();
                }
                else
                {
                    if (!await queueTask)
                    {
                        break; // 큐가 닫힘 (앱 종료 등)
                    }

                    while (_outboundQueue.Reader.TryRead(out var queued))
                    {
                        await requestStream.WriteAsync(queued, stoppingToken);
                    }

                    // 업무 메시지를 보냈으니 타이머를 리셋한다.
                    heartbeatTimer.Period = _options.HeartbeatInterval;
                    timerTask = heartbeatTimer.WaitForNextTickAsync(stoppingToken).AsTask();

                    queueTask = _outboundQueue.Reader.WaitToReadAsync(stoppingToken).AsTask();
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // 앱 종료에 의한 정상적인 취소 - 루프를 정상 종료한다.
        }
        finally
        {
            // 취소/종료/예외 어떤 경우든 스트림을 정상적으로 닫아준다.
            // 안 해주면 서버 쪽이 "클라이언트가 스트림을 제대로 닫지 않았다"고 에러로 인식할 수 있다.
            await requestStream.CompleteAsync();
        }
    }

    /// <summary>
    /// 마스터 → 메인 서버 방향. MasterMessage의 oneof로 하트비트/명령을 구분한다.
    /// 서버 쪽 ReceiveLoopAsync와 대칭으로, 마지막 메시지로부터 타임아웃이 지나면
    /// 마스터가 멈춘 것으로 간주하고 루프를 종료해 재연결을 트리거한다.
    /// </summary>
    private async Task ReceiveLoopAsync(
        IAsyncStreamReader<MasterMessage> responseStream,
        CancellationToken stoppingToken)
    {
        // 마스터가 KeepaliveInterval(5초) 주기로 메시지를 보내므로
        // 그 2~3배인 MasterHeartbeatTimeout을 타임아웃으로 쓴다.
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
                    // 앱 종료가 아니라 우리가 건 타임아웃이 발동한 경우.
                    _logger.LogWarning("마스터 서버 하트비트 타임아웃 - 재연결을 시도합니다.");
                    return;
                }

                if (!hasNext)
                {
                    return; // 마스터가 스트림을 정상적으로 닫음
                }

                var message = responseStream.Current;

                switch (message.PayloadCase)
                {
                    case MasterMessage.PayloadOneofCase.Heartbeat:
                        // idle heartbeat - 메시지 수신 자체가 생존 신호, 별도 처리 없음
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
            // 앱 종료에 의한 정상적인 취소 - 루프를 정상 종료한다.
        }
    }

    private void HandleCommand(MasterCommand command)
    {
        switch (command.CommandCase)
        {
            case MasterCommand.CommandOneofCase.Broadcast:
                _logger.LogInformation("운영 공지 수신: {Message}", command.Broadcast.Message);
                // TODO: 같은 페어의 인게임 서버로 릴레이, 접속 중인 클라이언트들에게 전달
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
