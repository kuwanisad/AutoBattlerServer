using Grpc.Core;
using MasterServer.Registry;
using MasterServer.Security;
using Shared.Common;
using Shared.Common.Redis;
using GameServer.Common;

namespace MasterServer.Services;

/// <summary>
/// Connect RPC의 실제 로직 담당.
/// 접속 시작 시 IServerAuthenticator로 접속자를 검증하고, 이후 ShutdownNotice 수신
/// 여부로 정상/비정상 종료를 구분해 로그에 남긴다.
/// (예전에는 비정상 종료 시 RegistryCleanupServer에 통보했으나, 서버 생존/권한 관리를
/// Redis TTL + TryClaimOwnershipAsync로 대체하면서 그 통보 경로는 제거했다.)
/// </summary>
internal sealed class ConnectionHandler
{
    private readonly InstanceRegistry _registry;
    private readonly IServerAuthenticator _authenticator;
    private readonly RedisOptions _redisOptions;
    private readonly ILogger _logger;

    private sealed class ShutdownFlag
    {
        public bool IsNormal { get; set; }
    }

    internal ConnectionHandler(
        InstanceRegistry registry,
        IServerAuthenticator authenticator,
        RedisOptions redisOptions,
        ILogger logger)
    {
        _registry = registry;
        _authenticator = authenticator;
        _redisOptions = redisOptions;
        _logger = logger;
    }

    internal async Task HandleAsync(
        IAsyncStreamReader<MainServerMessage> requestStream,
        IServerStreamWriter<MasterMessage> responseStream,
        ServerCallContext context)
    {
        var authResult = _authenticator.Authenticate(context);
        if (!authResult.IsAuthenticated)
        {
            _logger.LogWarning(
                "인증 실패로 접속 거부: {Peer} - {Reason}",
                context.Peer, authResult.FailureReason ?? "사유 없음");
            throw new RpcException(new Status(
                StatusCode.Unauthenticated, authResult.FailureReason ?? "인증 실패"));
        }

        var cancellationToken = context.CancellationToken;
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
                _logger.LogWarning(
                    "인스턴스 접속 종료 처리: {InstanceId} ({Reason})",
                    pendingInfo.InstanceId, shutdownFlag.IsNormal ? "정상 종료" : "비정상 종료");
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
        // 이 연결에서 아직 InstanceId가 없었다면 이번 Status가 최초 등록이다.
        // (재접속 시 최초 등록으로, 이후 상태 갱신 Status는 최초 등록이 아니다.)
        var isFirstRegistration = string.IsNullOrEmpty(pendingInfo.InstanceId);

        pendingInfo.InstanceId = status.InstanceId;
        pendingInfo.ServerType = status.ServerType;
        pendingInfo.Address = status.Address;
        pendingInfo.CurrentLoad = status.CurrentLoad;
        pendingInfo.Capacity = status.Capacity;

        _registry.Upsert(pendingInfo);

        _logger.LogInformation(
            "상태 수신: {InstanceId} ({ServerType}) 부하 {Load}/{Capacity}",
            status.InstanceId, status.ServerType, status.CurrentLoad, status.Capacity);

        if (!isFirstRegistration) return;

        // 기동 순서는 마스터 → 매칭 → 메인 서버들이므로, 메인 서버가 처음 접속하는
        // 시점에는 매칭 서버가 이미 레지스트리에 있어야 정상이다. 있으면 바로 알려준다.
        if (status.ServerType == ServerType.Matching)
        {
            BroadcastMatchingServerAddress(status.InstanceId, status.Address);
        }
        else
        {
            var matchingServer = _registry.GetByType(ServerType.Matching).FirstOrDefault();
            if (matchingServer != null)
            {
                _registry.Enqueue(status.InstanceId, new MasterMessage
                {
                    MatchingServer = new MatchingServerInfo { Address = matchingServer.Address },
                });

                _logger.LogInformation(
                    "매칭 서버 주소 전달: {InstanceId} ← {MatchingAddress}",
                    status.InstanceId, matchingServer.Address);
            }
            else
            {
                _logger.LogWarning(
                    "메인 서버 접속({InstanceId})했으나 매칭 서버가 아직 등록되지 않음. " +
                    "기동 순서(마스터→매칭→메인)를 확인하세요.",
                    status.InstanceId);
            }
        }

        // 계정 서버(AccountServer)도 매칭 서버와 완전히 같은 패턴 - 실제로는 로그인
        // 서버만 이 주소를 쓰지만, 다른 서버 타입인지 여부를 안 가리고 똑같이 브로드캐스트한다
        // (지금 매칭 서버 처리랑 구조적으로 일관성 유지 - 안 쓰는 서버는 그냥 무시하면 됨).
        if (status.ServerType == ServerType.Account)
        {
            BroadcastAccountServerAddress(status.InstanceId, status.Address);
        }
        else
        {
            var accountServer = _registry.GetByType(ServerType.Account).FirstOrDefault();
            if (accountServer != null)
            {
                _registry.Enqueue(status.InstanceId, new MasterMessage
                {
                    AccountServer = new AccountServerInfo { Address = accountServer.Address },
                });

                _logger.LogInformation(
                    "계정 서버 주소 전달: {InstanceId} ← {AccountAddress}",
                    status.InstanceId, accountServer.Address);
            }
            else
            {
                _logger.LogWarning(
                    "메인 서버 접속({InstanceId})했으나 계정 서버가 아직 등록되지 않음. " +
                    "로그인 서버라면 기동 순서를 확인하세요.",
                    status.InstanceId);
            }
        }

        // 세션 레지스트리(Redis) 주소는 마스터가 자체 설정(shared.appsettings.json)에서
        // 정적으로 알고 있으므로, 매칭 서버와 달리 등록 순서와 무관하게 항상 바로 전달한다.
        // 매칭 서버 자신도 ISessionRegistry를 쓰므로 타입 구분 없이 전달한다.
        _registry.Enqueue(status.InstanceId, new MasterMessage
        {
            SessionRegistry = new SessionRegistryInfo { Address = _redisOptions.ConnectionString },
        });

        _logger.LogInformation(
            "세션 레지스트리 주소 전달: {InstanceId} ← {Address}",
            status.InstanceId, _redisOptions.ConnectionString);
    }

    /// <summary>
    /// 매칭 서버가 (재)등록될 때, 이미 접속해 있던 메인 서버들에게 새 주소를 알린다.
    /// 정상적인 기동 순서(마스터→매칭→메인)라면 거의 발생하지 않지만, 매칭 서버가
    /// 재시작되어 주소가 바뀌는 경우를 대비한 안전망이다.
    /// </summary>
    private void BroadcastMatchingServerAddress(string matchingInstanceId, string matchingAddress)
    {
        var message = new MasterMessage
        {
            MatchingServer = new MatchingServerInfo { Address = matchingAddress },
        };

        var recipients = _registry.GetAll()
            .Where(i => i.ServerType != ServerType.Matching)
            .ToList();

        foreach (var instance in recipients)
        {
            _registry.Enqueue(instance.InstanceId, message);
        }

        _logger.LogInformation(
            "매칭 서버 등록: {InstanceId} ({Address}) - 접속 중인 메인 서버 {Count}개에 통보",
            matchingInstanceId, matchingAddress, recipients.Count);
    }

    /// <summary>매칭 서버와 같은 이유로 존재하는 계정 서버 버전. BroadcastMatchingServerAddress 참고.</summary>
    private void BroadcastAccountServerAddress(string accountInstanceId, string accountAddress)
    {
        var message = new MasterMessage
        {
            AccountServer = new AccountServerInfo { Address = accountAddress },
        };

        var recipients = _registry.GetAll()
            .Where(i => i.ServerType != ServerType.Account)
            .ToList();

        foreach (var instance in recipients)
        {
            _registry.Enqueue(instance.InstanceId, message);
        }

        _logger.LogInformation(
            "계정 서버 등록: {InstanceId} ({Address}) - 접속 중인 메인 서버 {Count}개에 통보",
            accountInstanceId, accountAddress, recipients.Count);
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
