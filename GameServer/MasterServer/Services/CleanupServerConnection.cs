using System.Threading.Channels;
using CleanupServerContracts;
using Grpc.Net.Client;
using Shared.Common;

namespace MasterServer.Services;

/// <summary>
/// 마스터 서버가 RegistryCleanupServer와 상시 연결을 유지하는 BackgroundService.
/// 하트비트로 연결을 유지하며, 서버 비정상 종료 감지 시 ServerDiedNotice를 전송한다.
/// </summary>
public sealed class CleanupServerConnection : BackgroundService
{
    private readonly ILogger<CleanupServerConnection> _logger;

    // TODO: appsettings.json으로 빼기
    private const string CleanupServerAddress = "http://localhost:9000";

    private readonly Channel<MasterCleanupMessage> _outboundQueue =
        Channel.CreateUnbounded<MasterCleanupMessage>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
        });

    private static readonly TimeSpan KeepaliveInterval = TimeSpan.FromSeconds(10);

    public CleanupServerConnection(ILogger<CleanupServerConnection> logger)
    {
        _logger = logger;
    }

    /// <summary>비정상 종료된 서버를 RegistryCleanupServer에 통보한다.</summary>
    public void NotifyServerDied(string serverId)
    {
        _outboundQueue.Writer.TryWrite(new MasterCleanupMessage
        {
            ServerDied = new ServerDiedNotice
            {
                ServerId = serverId,
                DetectedAtUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            },
        });

        _logger.LogInformation("서버 죽음 통보 큐 적재: {ServerId}", serverId);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ConnectAndRunAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                
                _logger.LogWarning(ex, "RegistryCleanupServer 연결 실패. {Delay}초 후 재시도...",
                    ConnectionConstants.ReconnectDelay.TotalSeconds);
                await Task.Delay(ConnectionConstants.ReconnectDelay, stoppingToken);
            }
        }
    }

    private async Task ConnectAndRunAsync(CancellationToken cancellationToken)
    {
        using var channel = GrpcChannel.ForAddress(CleanupServerAddress);
        var client = new MasterCleanupConnection.MasterCleanupConnectionClient(channel);

        using var call = client.Connect(cancellationToken: cancellationToken);
        _logger.LogInformation("RegistryCleanupServer 연결 완료: {Address}", CleanupServerAddress);

        var receiveTask = ReceiveLoopAsync(call.ResponseStream, cancellationToken);
        var sendTask = SendLoopAsync(call.RequestStream, cancellationToken);

        await Task.WhenAny(receiveTask, sendTask);
        _logger.LogWarning("RegistryCleanupServer 연결 끊김. 재연결 시도...");
    }

    private async Task ReceiveLoopAsync(
        Grpc.Core.IAsyncStreamReader<CleanupServerMessage> responseStream,
        CancellationToken cancellationToken)
    {
        try
        {
            while (await responseStream.MoveNext(cancellationToken))
            {
                var message = responseStream.Current;
                switch (message.PayloadCase)
                {
                    case CleanupServerMessage.PayloadOneofCase.Heartbeat:
                        break;

                    case CleanupServerMessage.PayloadOneofCase.Ready:
                        _logger.LogInformation("RegistryCleanupServer 준비 완료");
                        break;

                    case CleanupServerMessage.PayloadOneofCase.Completed:
                        var completed = message.Completed;
                        _logger.LogInformation(
                            "잔업 완료 보고: {ServerId} ({Count}개 정리)",
                            completed.ServerId, completed.CleanedObjectCount);
                        break;

                    default:
                        _logger.LogWarning("알 수 없는 메시지: {Case}", message.PayloadCase);
                        break;
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "ReceiveLoopAsync 종료");
        }
    }

    private async Task SendLoopAsync(
        Grpc.Core.IClientStreamWriter<MasterCleanupMessage> requestStream,
        CancellationToken cancellationToken)
    {
        try
        {
            var timerTask = Task.Delay(KeepaliveInterval, cancellationToken);
            var queueTask = _outboundQueue.Reader.WaitToReadAsync(cancellationToken).AsTask();

            while (!cancellationToken.IsCancellationRequested)
            {
                var completed = await Task.WhenAny(timerTask, queueTask);

                if (completed == timerTask)
                {
                    await requestStream.WriteAsync(new MasterCleanupMessage
                    {
                        Heartbeat = new MasterCleanupHeartbeat(),
                    }, cancellationToken);
                    timerTask = Task.Delay(KeepaliveInterval, cancellationToken);
                }
                else
                {
                    if (!await queueTask) return;

                    while (_outboundQueue.Reader.TryRead(out var message))
                        await requestStream.WriteAsync(message, cancellationToken);

                    timerTask = Task.Delay(KeepaliveInterval, cancellationToken);
                    queueTask = _outboundQueue.Reader.WaitToReadAsync(cancellationToken).AsTask();
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "SendLoopAsync 종료");
        }
    }
}
