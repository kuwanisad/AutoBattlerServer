using Grpc.Core;
using IngameServer;
using MainServer.Common.MasterConnection;
using MainServer.Common.MatchConnection;
using Microsoft.Extensions.Logging;
using Shared.Common;
using Shared.Common.Redis;

namespace GameAgent.Services;

/// <summary>
/// 게임 서버와의 연결 한 번의 생애주기를 담당한다.
/// MasterServer의 ConnectionHandler와 동일한 패턴.
///
/// 게임 서버가 연결되면:
/// 1. 송수신 루프를 시작한다.
/// 2. 게임 서버가 ServerReady를 보내면 마스터 서버에 접속하고, 매칭 서버 접속도 시작한다
///    (MatchServerConnection.SignalReady - 그 전까지는 매칭 후보가 되면 안 되므로 접속을 미뤄둔다).
/// 3. IngameMatchService가 RequestOpenRoomAsync를 호출하면 OpenRoom을 게임 서버로 보내고,
///    대응하는 RoomOpened가 올 때까지 기다린다 (roomId로 상관관계 추적).
/// 4. 게임 서버가 RoomOpened를 보내면 대기 중인 요청을 풀어준다.
/// 5. 게임 서버가 ApprovalRequest를 보내면 Redis에서 세션/권한 검증 후 ApprovalResponse를 반환한다.
///
/// 연결이 끊기면(하트비트 타임아웃 포함) 대기 중이던 모든 방 생성 요청을 일괄 실패 처리한다 -
/// 보고할 곳이 없어졌으니, 계산이 끝났든 안 끝났든 전부 무효로 본다.
///
/// 매칭 서버에서의 해제(연결 종료 시)는 이제 별도 RPC 호출이 필요 없다 - MatchServerConnection의
/// 스트림이 이 프로세스 종료와 함께 끊기면 매칭 서버가 즉시 서버 풀에서 제거한다.
/// </summary>
public sealed class GameServerHandler
{
    private readonly MasterConnectionService _masterConnection;
    private readonly MatchServerConnection _matchServerConnection;
    private readonly ISessionRegistry _sessionRegistry;
    private readonly ILogger<GameServerHandler> _logger;
    private GameServerInfo? _serverInfo;

    // 게임 서버와의 비동기 왕복 업무 추적. 종류가 늘어나도 이 필드 하나로 다 담는다.
    private readonly PendingWorkTracker _pendingWork = new();

    public GameServerHandler(
        MasterConnectionService masterConnection,
        MatchServerConnection matchServerConnection,
        ISessionRegistry sessionRegistry,
        ILogger<GameServerHandler> logger)
    {
        _masterConnection = masterConnection;
        _matchServerConnection = matchServerConnection;
        _sessionRegistry = sessionRegistry;
        _logger = logger;
    }

    /// <summary>
    /// 게임 서버에 방 생성을 요청하고, RoomOpened 응답이 올 때까지 기다린다.
    /// IngameMatchService.PrepareMatch가 이 메서드를 호출한다.
    /// 연결이 끊기면(하트비트 타임아웃 포함) 이 Task는 예외로 끝난다 - HandleAsync의
    /// finally에서 대기 중이던 모든 요청을 일괄 실패 처리하기 때문이다.
    /// </summary>
    public Task<RoomOpened> RequestOpenRoomAsync(
        string roomId, IReadOnlyList<string> playerIds, TimeSpan timeout, CancellationToken cancellationToken)
    {
        if (_serverInfo == null || !_serverInfo.IsReady)
        {
            throw new InvalidOperationException("게임 서버가 준비되지 않았습니다.");
        }

        // WaitForAsync를 호출하면 그 안의 등록(TryAdd)까지는 동기적으로 끝난다
        // (C# async 메서드는 첫 진짜 await 전까지 호출한 스레드에서 그대로 실행된다).
        // 그러니 메시지를 보내기 전에 반드시 이 호출을 먼저 해서 등록부터 확정해야 한다 -
        // 순서가 바뀌면 응답이 극단적으로 빨리 와서 등록 전에 도착해버리는 경합이 생긴다.
        var waitTask = _pendingWork.WaitForAsync<RoomOpened>(roomId, timeout, cancellationToken);

        var written = _serverInfo.OutboundQueue.Writer.TryWrite(new GameAgentMessage
        {
            OpenRoom = new OpenRoom
            {
                RoomId = roomId,
                PlayerIds = { playerIds },
                TimestampUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            },
        });

        if (!written)
        {
            throw new InvalidOperationException("게임 서버에 메시지를 보낼 수 없습니다 (큐 닫힘).");
        }

        return waitTask;
    }

    public async Task HandleAsync(
        IAsyncStreamReader<GameServerMessage> requestStream,
        IServerStreamWriter<GameAgentMessage> responseStream,
        CancellationToken cancellationToken)
    {
        _serverInfo = new GameServerInfo();
        _logger.LogInformation("게임 서버 연결됨");

        var receiveTask = ReceiveLoopAsync(requestStream, responseStream, _serverInfo, cancellationToken);
        var sendTask = SendLoopAsync(responseStream, _serverInfo, cancellationToken);

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

            // 보고할 곳이 없어졌다 - 계산이 끝났든 안 끝났든, 대기 중이던 업무는
            // 전부 무효로 본다 (개별 결과를 살릴 방법이 없으므로 일괄 실패 처리).
            _pendingWork.Cleanup("게임 서버 연결이 끊겼습니다.");

            if (_serverInfo.IsReady)
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

        // 매칭 서버 접속 시작 - 그 전까지는 매칭 후보가 되면 안 되므로 여기서 신호를 준다.
        _matchServerConnection.SignalReady();
        _logger.LogInformation("매칭 서버 접속 신호 전송");

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

        if (_pendingWork.TryComplete(roomOpened.RoomId, roomOpened))
        {
            return;
        }

        _logger.LogWarning("대기 중이 아닌 RoomOpened 수신 (이미 타임아웃/연결끊김으로 처리됐을 수 있음): RoomId={RoomId}",
            roomOpened.RoomId);
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
        else
        {
            // 권한 이전 요청 - 기존 서버(로비)가 죽었거나 이미 이전됐으면 승인
            var claimed = await _sessionRegistry.TryClaimOwnershipAsync(
                _masterConnection.InstanceId, request.PlayerId, session);

            if (!claimed)
            {
                response.Approved = false;
                response.Reason = "권한 이전 거부 - 기존 서버 아직 살아있음";
                _logger.LogWarning("접속 거부 - 권한 이전 거부: PlayerId={PlayerId}", request.PlayerId);
            }
            else
            {
                response.Approved = true;
                serverInfo.AddClientMapping(request.ClientId, request.PlayerId);
                _logger.LogInformation("접속 승인: ClientId={ClientId} PlayerId={PlayerId}",
                    request.ClientId, request.PlayerId);
            }
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
            session.OwnerServerId = _masterConnection.InstanceId;
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
            session.OwnerServerId = string.Empty;
            await _sessionRegistry.UpdateAsync(left.PlayerId, session);
        }
    }

    private static GameAgentMessage MakeHeartbeat() => new()
    {
        Heartbeat = new GameAgentHeartbeat(),
    };

    public GameServerInfo? GetServerInfo() => _serverInfo;
}