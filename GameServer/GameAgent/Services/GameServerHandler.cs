using Grpc.Core;
using IngameServer;
using MainServer.Common.MasterConnection;
using Microsoft.Extensions.Logging;
using Shared.Common.Redis;

namespace GameAgent.Services;

/// <summary>
/// 게임 서버와의 연결 한 번의 생애주기를 담당한다.
/// MasterServer의 ConnectionHandler와 동일한 패턴.
///
/// 게임 서버가 연결되면:
/// 1. 송수신 루프를 시작한다.
/// 2. 게임 서버가 ServerReady를 보내면 마스터 서버에 접속한다.
/// 3. 마스터에서 OpenRoom이 오면 게임 서버에 릴레이한다.
/// 4. 게임 서버가 RoomOpened를 보내면 마스터에 보고한다.
/// 5. 게임 서버가 ApprovalRequest를 보내면 Redis에서 세션/토큰 검증 후 ApprovalResponse를 반환한다.
/// </summary>
public sealed class GameServerHandler
{
    private readonly MasterConnectionService _masterConnection;
    private readonly ISessionRegistry _sessionRegistry;
    private readonly ILogger<GameServerHandler> _logger;

    public GameServerHandler(
        MasterConnectionService masterConnection,
        ISessionRegistry sessionRegistry,
        ILogger<GameServerHandler> logger)
    {
        _masterConnection = masterConnection;
        _sessionRegistry = sessionRegistry;
        _logger = logger;
    }

    public async Task HandleAsync(
        IAsyncStreamReader<GameServerMessage> requestStream,
        IServerStreamWriter<GameAgentMessage> responseStream,
        CancellationToken cancellationToken)
    {
        var serverInfo = new GameServerInfo();
        _logger.LogInformation("게임 서버 연결됨");

        var receiveTask = ReceiveLoopAsync(requestStream, responseStream, serverInfo, cancellationToken);
        var sendTask = SendLoopAsync(responseStream, serverInfo, cancellationToken);

        var completedTask = await Task.WhenAny(receiveTask, sendTask);
        var remainingTask = completedTask == receiveTask ? sendTask : receiveTask;

        _ = remainingTask.ContinueWith(
            t => _logger.LogDebug(t.Exception, "남아있던 스트림 작업이 정리됐습니다."),
            TaskScheduler.Default);

        try
        {
            await completedTask;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        finally
        {
            _logger.LogInformation("게임 서버 연결 종료");

            if (serverInfo.IsReady)
            {
                _masterConnection.UpdateStatus(currentLoad: 0, capacity: 0);
            }
        }
    }

    private async Task ReceiveLoopAsync(
        IAsyncStreamReader<GameServerMessage> requestStream,
        IServerStreamWriter<GameAgentMessage> responseStream,
        GameServerInfo serverInfo,
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
                    _logger.LogWarning("게임 서버 하트비트 타임아웃 - 연결을 종료합니다.");
                    return;
                }

                if (!hasNext) return;

                var message = requestStream.Current;

                switch (message.PayloadCase)
                {
                    case GameServerMessage.PayloadOneofCase.Heartbeat:
                        break;

                    case GameServerMessage.PayloadOneofCase.Status:
                        HandleStatus(message.Status, serverInfo);
                        break;

                    case GameServerMessage.PayloadOneofCase.Ready:
                        await HandleReadyAsync(message.Ready, serverInfo);
                        break;

                    case GameServerMessage.PayloadOneofCase.RoomOpened:
                        HandleRoomOpened(message.RoomOpened);
                        break;

                    case GameServerMessage.PayloadOneofCase.MatchResult:
                        await HandleMatchResultAsync(message.MatchResult);
                        break;

                    case GameServerMessage.PayloadOneofCase.PlayerJoined:
                        await HandlePlayerJoinedAsync(message.PlayerJoined, serverInfo);
                        break;

                    case GameServerMessage.PayloadOneofCase.PlayerLeft:
                        await HandlePlayerLeftAsync(message.PlayerLeft, serverInfo);
                        break;

                    case GameServerMessage.PayloadOneofCase.ApprovalRequest:
                        await HandleApprovalRequestAsync(message.ApprovalRequest, serverInfo);
                        break;

                    case GameServerMessage.PayloadOneofCase.None:
                        break;

                    default:
                        _logger.LogWarning("알 수 없는 GameServerMessage 종류: {Case}", message.PayloadCase);
                        break;
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
    }

    private async Task SendLoopAsync(
        IServerStreamWriter<GameAgentMessage> responseStream,
        GameServerInfo serverInfo,
        CancellationToken cancellationToken)
    {
        try
        {
            var timerTask = Task.Delay(ConnectionConstants.KeepaliveInterval, cancellationToken);
            var queueTask = serverInfo.OutboundQueue.Reader.WaitToReadAsync(cancellationToken).AsTask();

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

                    while (serverInfo.OutboundQueue.Reader.TryRead(out var queued))
                    {
                        _logger.LogInformation("메시지 전송: {Type}", queued.PayloadCase);
                        await responseStream.WriteAsync(queued, cancellationToken);
                    }

                    timerTask = Task.Delay(ConnectionConstants.KeepaliveInterval, cancellationToken);
                    queueTask = serverInfo.OutboundQueue.Reader.WaitToReadAsync(cancellationToken).AsTask();
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
    }

    private void HandleStatus(GameServerStatus status, GameServerInfo serverInfo)
    {
        serverInfo.CurrentLoad = status.CurrentLoad;
        serverInfo.Capacity = status.Capacity;

        if (serverInfo.IsReady)
        {
            _masterConnection.UpdateStatus(status.CurrentLoad, status.Capacity);
        }

        _logger.LogDebug("게임 서버 상태: {Load}/{Capacity}", status.CurrentLoad, status.Capacity);
    }

    private async Task HandleReadyAsync(ServerReady ready, GameServerInfo serverInfo)
    {
        serverInfo.IsReady = true;
        serverInfo.Capacity = ready.Capacity;

        _logger.LogInformation("게임 서버 준비 완료. Capacity={Capacity}", ready.Capacity);

        _masterConnection.UpdateStatus(currentLoad: 0, capacity: ready.Capacity);
        await Task.CompletedTask;
    }

    private void HandleRoomOpened(RoomOpened roomOpened)
    {
        if (roomOpened.Success)
        {
            _logger.LogInformation("방 열기 완료: RoomId={RoomId}", roomOpened.RoomId);
        }
        else
        {
            _logger.LogError("방 열기 실패: RoomId={RoomId} Error={Error}",
                roomOpened.RoomId, roomOpened.ErrorMessage);
        }
    }

    private async Task HandleMatchResultAsync(MatchResult result)
    {
        _logger.LogInformation("매치 결과 수신: MatchId={MatchId}", result.MatchId);
        await Task.CompletedTask;
    }

    private async Task HandleApprovalRequestAsync(ApprovalRequest request, GameServerInfo serverInfo)
    {
        _logger.LogInformation("접속 검증 요청: ClientId={ClientId} PlayerId={PlayerId}",
            request.ClientId, request.PlayerId);

        var response = new ApprovalResponse
        {
            ClientId = request.ClientId,
            PlayerId = request.PlayerId,
        };

        var session = await _sessionRegistry.GetAsync(request.PlayerId);

        if (session == null)
        {
            response.Approved = false;
            response.Reason = "세션 없음";
            _logger.LogWarning("접속 거부 - 세션 없음: PlayerId={PlayerId}", request.PlayerId);
        }
        else if (session.Status != PlayerStatus.Moving)
        {
            response.Approved = false;
            response.Reason = $"잘못된 세션 상태: {session.Status}";
            _logger.LogWarning("접속 거부 - 잘못된 상태: PlayerId={PlayerId} Status={Status}",
                request.PlayerId, session.Status);
        }
        else if (session.MatchToken != request.MatchToken)
        {
            response.Approved = false;
            response.Reason = "매치 토큰 불일치";
            _logger.LogWarning("접속 거부 - 토큰 불일치: PlayerId={PlayerId}", request.PlayerId);
        }
        else
        {
            response.Approved = true;
            serverInfo.AddClientMapping(request.ClientId, request.PlayerId);
            _logger.LogInformation("접속 승인: ClientId={ClientId} PlayerId={PlayerId}",
                request.ClientId, request.PlayerId);
        }

        var written = serverInfo.OutboundQueue.Writer.TryWrite(new GameAgentMessage
        {
            ApprovalResponse = response,
        });
    }

    private async Task HandlePlayerJoinedAsync(PlayerJoined joined, GameServerInfo serverInfo)
    {
        serverInfo.CurrentLoad++;
        _masterConnection.UpdateStatus(serverInfo.CurrentLoad, serverInfo.Capacity);
        _logger.LogInformation("플레이어 접속: {PlayerId} ({Load}/{Capacity})",
            joined.PlayerId, serverInfo.CurrentLoad, serverInfo.Capacity);

        var session = await _sessionRegistry.GetAsync(joined.PlayerId);
        if (session != null)
        {
            session.Status = PlayerStatus.Ingame;
            session.CurrentServer = joined.RoomId;
            session.NextServer = string.Empty;
            session.MatchToken = string.Empty;
            await _sessionRegistry.UpdateAsync(joined.PlayerId, session);
        }
        else
        {
            _logger.LogWarning("세션 없는 플레이어 접속: {PlayerId}", joined.PlayerId);
        }
    }

    private async Task HandlePlayerLeftAsync(PlayerLeft left, GameServerInfo serverInfo)
    {
        serverInfo.CurrentLoad = Math.Max(0, serverInfo.CurrentLoad - 1);
        _masterConnection.UpdateStatus(serverInfo.CurrentLoad, serverInfo.Capacity);
        _logger.LogInformation("플레이어 퇴장: {PlayerId} ({Load}/{Capacity})",
            left.PlayerId, serverInfo.CurrentLoad, serverInfo.Capacity);

        serverInfo.RemoveClientMapping(left.PlayerId);

        var session = await _sessionRegistry.GetAsync(left.PlayerId);
        if (session != null)
        {
            session.Status = PlayerStatus.Moving;
            session.CurrentServer = string.Empty;
            session.NextServer = "lobby";
            await _sessionRegistry.UpdateAsync(left.PlayerId, session);
        }
    }

    private static GameAgentMessage MakeHeartbeat() => new()
    {
        Heartbeat = new GameAgentHeartbeat(),
    };
}
