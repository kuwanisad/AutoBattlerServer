using System.Threading.Channels;
using Grpc.Core;
using Grpc.Net.Client;
using GameServer.Common;
using MasterServer;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Shared.Common.Redis;

namespace MainServer.Common.MasterConnection;

/// <summary>
/// 마스터 서버와의 Connect 양방향 스트림을 평생 유지하려고 시도하는 백그라운드 서비스.
/// 연결이 끊기면 지수 백오프로 재시도한다. 지금은 "마스터에 붙는 것"만 다루고,
/// 실제 매칭/클라이언트 처리 로직은 아직 없다 (TODO로 표시된 자리에 추후 채울 예정).
/// </summary>
public sealed class MasterConnectionService : BackgroundService
{
    private readonly MasterConnectionOptions _options;
    private readonly ISessionRegistryConnector _sessionRegistryConnector;
    private readonly IHostApplicationLifetime _appLifetime;
    private readonly ILogger<MasterConnectionService> _logger;

    /// <summary>마스터 서버로부터 받은 매칭 서버 주소.</summary>
    public string MatchingServerAddress { get; private set; } = string.Empty;

    /// <summary>마스터 서버로부터 받은 계정 서버(AccountServer) 주소. 실제로는 로그인 서버만 씀.</summary>
    public string AccountServerAddress { get; private set; } = string.Empty;

    /// <summary>마스터 서버로부터 받은 채팅 서버 주소. 실제로는 로그인 서버만 씀
    /// (로그인 성공 응답에 실어서 클라에 전달).</summary>
    public string ChatServerAddress { get; private set; } = string.Empty;

    /// <summary>
    /// 마스터 서버로부터 받은 세션 레지스트리(Redis) 원본 주소. ISessionRegistry를 쓰는
    /// 코드는 이걸 직접 몰라도 되지만(SessionRegistryConnector가 알아서 연결), 세션
    /// 레지스트리와 완전히 별개의 용도(예: 채팅 서버의 방/메시지 저장)로 자기만의 Redis
    /// 연결을 새로 열어야 하는 경우를 위해 노출해둔다 - "채팅방을 세션 레지스트리에
    /// 등록하면 안 된다"는 원칙에 따라 ISessionRegistry API 자체는 절대 안 거치지만,
    /// 같은 물리적 Redis 서버를 다른 키스페이스로 쓰는 건 무방하다.
    /// </summary>
    public string RedisAddress { get; private set; } = string.Empty;

    /// <summary>이 서버 인스턴스의 고유 ID.</summary>
    public string InstanceId => _options.InstanceId;

    /// <summary>다른 서버가 이 인스턴스에 접속할 주소.</summary>
    public string SelfAddress => _options.SelfAddress;

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
        ISessionRegistryConnector sessionRegistryConnector,
        IHostApplicationLifetime appLifetime,
        ILogger<MasterConnectionService> logger)
    {
        _options = options.Value;
        _sessionRegistryConnector = sessionRegistryConnector;
        _appLifetime = appLifetime;
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

                    case MasterMessage.PayloadOneofCase.Instruction:
                        HandleInstruction(message.Instruction, stoppingToken);
                        break;

                    case MasterMessage.PayloadOneofCase.MatchingServer:
                        MatchingServerAddress = message.MatchingServer.Address;
                        _logger.LogInformation("매칭 서버 주소 수신: {Address}", MatchingServerAddress);
                        break;

                    case MasterMessage.PayloadOneofCase.SessionRegistry:
                        // 로비/게임 에이전트/채팅/매칭 서버 전부 세션 레지스트리(Redis)가 필요하므로
                        // 서버 타입 구분 없이 받는 즉시 연결을 요청한다.
                        RedisAddress = message.SessionRegistry.Address;
                        _ = ConnectSessionRegistryAsync(message.SessionRegistry.Address, stoppingToken);
                        break;

                    case MasterMessage.PayloadOneofCase.AccountServer:
                        // 실제로는 로그인 서버만 이 주소를 쓰지만, 매칭 서버 주소와 같은
                        // 방식으로 전체 메인 서버에 동일하게 브로드캐스트한다 - 지금 구조와
                        // 일관성 유지, 안 쓰는 서버는 그냥 무시.
                        AccountServerAddress = message.AccountServer.Address;
                        _logger.LogInformation("계정 서버 주소 수신: {Address}", AccountServerAddress);
                        break;

                    case MasterMessage.PayloadOneofCase.ChatServer:
                        // 실제로는 로그인 서버만 이 주소를 쓴다(로그인 성공 응답에 실어서
                        // 클라에 전달) - AccountServer와 같은 패턴.
                        ChatServerAddress = message.ChatServer.Address;
                        _logger.LogInformation("채팅 서버 주소 수신: {Address}", ChatServerAddress);
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

    /// <summary>
    /// 서버별로 종료 전에 정리할 게 있으면(예: 매칭 서버는 신규 대기열 등록 거부, 로비는
    /// EnterLobby 거부) 이 이벤트를 구독해서 처리할 수 있다. 아무도 구독 안 하면 기본
    /// 동작(유예 시간만큼 대기 후 그냥 종료)만 일어난다.
    /// </summary>
    public event Action<ShutdownTask>? OnShutdownRequested;

    /// <summary>
    /// 공지 방송 지시 - active=true면 (1) 지금 접속 중인 클라 전원한테 즉시 방송하고
    /// (2) 앞으로 새로 접속하는 클라한테도 계속 전달하도록 등록, active=false면 그
    /// 등록만 해제한다. 실제 등록/해제(예: OnClientConnected 델리게이트) 및 스냅샷
    /// 방송은 서버별로 성격이 달라서(로비는 gRPC 스트림, 인게임은 NGO, 채팅은 웹소켓)
    /// 각 서버가 직접 구독해서 처리해야 한다 - 여기선 그냥 신호만 중계한다.
    /// </summary>
    public event Action<AnnouncementTask>? OnAnnouncementRequested;

    private void HandleInstruction(MasterServerInstruction instruction, CancellationToken stoppingToken)
    {
        switch (instruction.TaskCase)
        {
            case MasterServerInstruction.TaskOneofCase.Shutdown:
                HandleShutdown(instruction.Shutdown, stoppingToken);
                break;

            case MasterServerInstruction.TaskOneofCase.Announcement:
                _logger.LogInformation("마스터로부터 공지 지시 수신: Active={Active} Type={Type}",
                    instruction.Announcement.Active, instruction.Announcement.AnnouncementType);
                OnAnnouncementRequested?.Invoke(instruction.Announcement);
                break;

            default:
                _logger.LogWarning("알 수 없는 MasterServerInstruction 종류: {Case}", instruction.TaskCase);
                break;
        }
    }

    private void HandleShutdown(ShutdownTask task, CancellationToken stoppingToken)
    {
        _logger.LogWarning("마스터로부터 종료 지시 수신: 사유={Reason} 유예={GraceMs}ms",
            task.Reason, task.GracePeriodMs);

        OnShutdownRequested?.Invoke(task);

        // fire-and-forget - 이 메서드 자체는 수신 루프를 막으면 안 되므로 대기를 별도
        // Task로 분리한다. 함수 전체를 감싸는 이유는 아무도 기다리지 않는 fire-and-forget이라
        // 예외가 새면 조용히 사라지기 때문(ARCHITECTURE.md "코딩 관례" 참고).
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(TimeSpan.FromMilliseconds(task.GracePeriodMs), stoppingToken);
            }
            catch (OperationCanceledException)
            {
                // 정상 - 유예 시간 다 채우기 전에 서버가 어차피 종료 절차를 시작함
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "종료 유예 대기 중 예외");
            }

            _logger.LogWarning("종료 유예 시간 종료 - 프로세스를 정상 종료합니다.");
            _appLifetime.StopApplication();
        });
    }

    /// <summary>
    /// 마스터로부터 세션 레지스트리(Redis) 주소를 받으면 SessionRegistryConnector에
    /// 실제 연결을 요청한다. 이 호출이 끝나기 전까지 ISessionRegistry를 쓰는 코드는
    /// 전부 대기 상태로 남아있다가, 연결되는 순간 한꺼번에 풀린다.
    /// </summary>
    private async Task ConnectSessionRegistryAsync(string address, CancellationToken stoppingToken)
    {
        try
        {
            await _sessionRegistryConnector.ConnectAsync(address, stoppingToken);
            _logger.LogInformation("세션 레지스트리 연결 완료: {Address}", address);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // 앱 종료 중 정상 취소
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "세션 레지스트리 연결 실패: {Address}", address);
        }
    }
}