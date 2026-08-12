using CleanupServerContracts;
using Grpc.Core;
using RegistryCleanupServer.Services;

namespace RegistryCleanupServer.Services;

/// <summary>
/// 마스터 서버와 양방향 스트림으로 연결된다.
/// 마스터 서버로부터 서버 죽음 통보를 받아 OwnershipRegistry에 잔업을 위임한다.
/// </summary>
public sealed class MasterLinkService : MasterCleanupConnection.MasterCleanupConnectionBase
{
    private readonly OwnershipRegistry _registry;
    private readonly ILogger<MasterLinkService> _logger;

    private static readonly TimeSpan HeartbeatTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan KeepaliveInterval = TimeSpan.FromSeconds(10);

    public MasterLinkService(OwnershipRegistry registry, ILogger<MasterLinkService> logger)
    {
        _registry = registry;
        _logger = logger;
    }

    public override async Task Connect(
        IAsyncStreamReader<MasterCleanupMessage> requestStream,
        IServerStreamWriter<CleanupServerMessage> responseStream,
        ServerCallContext context)
    {
        _logger.LogInformation("마스터 서버 연결됨");

        var receiveTask = ReceiveLoopAsync(requestStream, responseStream, context.CancellationToken);
        var sendTask = SendLoopAsync(responseStream, context.CancellationToken);

        var completedTask = await Task.WhenAny(receiveTask, sendTask);

        try { await completedTask; }
        catch (OperationCanceledException) when (context.CancellationToken.IsCancellationRequested) { }
        finally
        {
            _logger.LogInformation("마스터 서버 연결 종료");
        }
    }

    private async Task ReceiveLoopAsync(
        IAsyncStreamReader<MasterCleanupMessage> requestStream,
        IServerStreamWriter<CleanupServerMessage> responseStream,
        CancellationToken cancellationToken)
    {
        try
        {
            while (true)
            {
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeoutCts.CancelAfter(HeartbeatTimeout);

                bool hasNext;
                try
                {
                    hasNext = await requestStream.MoveNext(timeoutCts.Token);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    _logger.LogWarning("마스터 서버 하트비트 타임아웃 - 연결을 종료합니다.");
                    return;
                }

                if (!hasNext) return;

                var message = requestStream.Current;

                switch (message.PayloadCase)
                {
                    case MasterCleanupMessage.PayloadOneofCase.Heartbeat:
                        break;

                    case MasterCleanupMessage.PayloadOneofCase.ServerDied:
                        await HandleServerDiedAsync(message.ServerDied, responseStream, cancellationToken);
                        break;

                    default:
                        _logger.LogWarning("알 수 없는 메시지: {Case}", message.PayloadCase);
                        break;
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
    }

    private async Task SendLoopAsync(
        IServerStreamWriter<CleanupServerMessage> responseStream,
        CancellationToken cancellationToken)
    {
        await responseStream.WriteAsync(new CleanupServerMessage
        {
            Ready = new CleanupServerReady
            {
                TimestampUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            },
        }, cancellationToken);

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(KeepaliveInterval, cancellationToken);
                await responseStream.WriteAsync(new CleanupServerMessage
                {
                    Heartbeat = new CleanupServerHeartbeat(),
                }, cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
    }

    private async Task HandleServerDiedAsync(
        ServerDiedNotice notice,
        IServerStreamWriter<CleanupServerMessage> responseStream,
        CancellationToken cancellationToken)
    {
        _logger.LogInformation("서버 죽음 통보: {ServerId}", notice.ServerId);

        var cleanedCount = await _registry.CleanupDeadServerAsync(notice.ServerId);

        await responseStream.WriteAsync(new CleanupServerMessage
        {
            Completed = new CleanupCompleted
            {
                ServerId = notice.ServerId,
                CleanedObjectCount = cleanedCount,
                CompletedAtUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                Success = true,
            },
        }, cancellationToken);
    }
}
