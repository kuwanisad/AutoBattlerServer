using Grpc.Core;
using Grpc.Net.Client;
using AccountServer;
using IngameServer;
using MainServer.Common.MasterConnection;
using MainServer.Common.MatchConnection;
using MainServer.Common.Messaging;
using MatchingServer;
using Microsoft.Extensions.Logging;
using Shared.Common;
using Shared.Common.Auth;
using Shared.Common.Messaging;
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
/// 6. 매칭 서버가 CancelRoom을 요청하면(매치가 중단됨) 그 방에 접속 중인 클라를 파악해서,
///    있으면 매칭 서버에 로비 주소를 물어 세션 권한을 이전한 뒤 리다이렉트 정보와 함께
///    게임 서버에 CloseRoom을 전달한다.
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
    private readonly ITokenProvider _tokenProvider;
    private readonly RabbitMqProducer _rabbitMqProducer;
    private readonly ILogger<GameServerHandler> _logger;
    private GameServerInfo? _serverInfo;

    // ServerReady로 게임 서버(Unity)가 알려준, 클라이언트가 NGO로 실제 접속할 주소.
    // _masterConnection.SelfAddress(게임 에이전트 자신의 gRPC 주소)와는 완전히 다른 값이다.
    private string _clientAddress = string.Empty;

    /// <summary>PrepareMatchResult 등에서 클라이언트에게 알려줄 실제 인게임 접속 주소.</summary>
    public string ClientAddress => _clientAddress;

    // 게임 서버와의 비동기 왕복 업무 추적. 종류가 늘어나도 이 필드 하나로 다 담는다.
    private readonly PendingWorkTracker _pendingWork = new();

    // 재접속 가능 여부 등 룰을 게임 타입별로 결정한다. 지금은 하드코딩된 정책이지만,
    // 나중에 설정 파일/마스터에서 받아오는 방식으로 바뀔 수 있다.
    private static readonly TimeSpan ReconnectGracePeriod = TimeSpan.FromSeconds(30);

    private static bool GetAllowReconnect(GameServer.Common.GameType gameType) => gameType switch
    {
        GameServer.Common.GameType.Normal => true,
        _ => false,
    };

    // roomId(=matchId) → 그 방에 지금 NGO로 접속해있는 playerId 목록.
    // CancelRoom이 왔을 때 "누구를 어디로 리다이렉트해야 하는지" 판단하는 데 쓴다.
    // 게임 서버 수신 루프(HandlePlayerJoinedAsync/Left)와 CancelRoom RPC(별도 gRPC 호출
    // 컨텍스트, 다른 스레드일 수 있음) 양쪽에서 건드리므로 락으로 보호한다.
    private readonly object _roomPlayersLock = new();
    private readonly Dictionary<string, HashSet<string>> _roomPlayers = new();

    // roomId → 그 방의 GameType. OpenRoom 보낼 때 기록해두고, HandlePlayerLeftAsync에서
    // 재접속 허용 여부를 판단하는 데 쓴다. 같은 락으로 보호한다.
    private readonly Dictionary<string, GameServer.Common.GameType> _roomGameType = new();

    public GameServerHandler(
        MasterConnectionService masterConnection,
        MatchServerConnection matchServerConnection,
        ISessionRegistry sessionRegistry,
        ITokenProvider tokenProvider,
        RabbitMqProducer rabbitMqProducer,
        ILogger<GameServerHandler> logger)
    {
        _masterConnection = masterConnection;
        _matchServerConnection = matchServerConnection;
        _sessionRegistry = sessionRegistry;
        _tokenProvider = tokenProvider;
        _rabbitMqProducer = rabbitMqProducer;
        _logger = logger;
    }

    /// <summary>
    /// 게임 서버에 방 생성을 요청하고, RoomOpened 응답이 올 때까지 기다린다.
    /// IngameMatchService.PrepareMatch가 이 메서드를 호출한다.
    /// 연결이 끊기면(하트비트 타임아웃 포함) 이 Task는 예외로 끝난다 - HandleAsync의
    /// finally에서 대기 중이던 모든 요청을 일괄 실패 처리하기 때문이다.
    /// </summary>
    public Task<RoomOpened> RequestOpenRoomAsync(
        string roomId, IReadOnlyList<string> playerIds, GameServer.Common.GameType gameType,
        TimeSpan timeout, CancellationToken cancellationToken)
    {
        if (_serverInfo == null || !_serverInfo.IsReady)
        {
            throw new InvalidOperationException("게임 서버가 준비되지 않았습니다.");
        }

        lock (_roomPlayersLock)
        {
            _roomGameType[roomId] = gameType;
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
                GameType = gameType,
            },
        });

        if (!written)
        {
            throw new InvalidOperationException("게임 서버에 메시지를 보낼 수 없습니다 (큐 닫힘).");
        }

        return waitTask;
    }

    /// <summary>
    /// 방을 닫고 그 안에 접속 중이던 클라들을 로비로 리다이렉트한다. 이유(취소든 정상 종료든)와
    /// 무관하게 "방을 닫고 다 같이 로비로 보낸다"는 메커니즘 자체는 동일해서 하나로 통일했다 -
    /// 매칭 서버의 CancelRoom 요청(IngameMatchService), 게임 서버 자체 방-채움 타임아웃
    /// (RoomTimeout), 매치 정상 종료(MatchResult) 전부 이 메서드를 거친다.
    /// 접속 중인 클라를 파악해서, 있으면 매칭 서버에 로비 주소를 물어 세션 권한을 이전한 뒤
    /// 리다이렉트 정보와 함께 게임 서버에 CloseRoom을 보낸다. 접속 중인 클라가 없으면(또는
    /// 리다이렉트할 로비를 못 찾으면) 그냥 방 제거만 요청한다.
    /// </summary>
    public async Task CloseRoomAndRedirectAsync(string matchId, string reason)
    {
        var connectedPlayerIds = new List<string>();
        lock (_roomPlayersLock)
        {
            if (_roomPlayers.TryGetValue(matchId, out var players))
            {
                connectedPlayerIds.AddRange(players);
            }
            _roomPlayers.Remove(matchId);
            _roomGameType.Remove(matchId);
        }

        var redirects = new List<PlayerRedirect>();

        if (connectedPlayerIds.Count > 0)
        {
            var lobby = await RequestLobbyAddressAsync();

            if (lobby != null)
            {
                foreach (var playerId in connectedPlayerIds)
                {
                    var session = await _sessionRegistry.GetAsync(playerId);
                    if (session == null) continue;

                    // 기존 소유자(이 게임 에이전트)가 살아있는 채로 자발적으로 넘겨주는 것이므로
                    // TryClaimOwnershipAsync의 "기존 소유자 생존 확인" 절차를 그대로 타면 오히려
                    // 거부당한다(자기 자신이 아직 살아있으니까) - 대신 새 소유자로 직접 갱신한다.
                    session.Status = PlayerStatus.Moving;
                    session.CurrentServer = string.Empty;
                    session.NextServer = lobby.Value.Address;
                    session.OwnerServerId = lobby.Value.ServerId;
                    await _sessionRegistry.UpdateAsync(playerId, session);

                    redirects.Add(new PlayerRedirect
                    {
                        PlayerId = playerId,
                        LobbyAddress = lobby.Value.Address,
                    });
                }

                _logger.LogInformation("방 종료 - {Count}명 리다이렉트 준비 완료: MatchId={MatchId} → {Address} ({Reason})",
                    redirects.Count, matchId, lobby.Value.Address, reason);
            }
            else
            {
                _logger.LogWarning("방 종료 - 리다이렉트할 로비를 찾지 못함: MatchId={MatchId} ({Reason})", matchId, reason);
            }
        }

        if (_serverInfo != null)
        {
            _serverInfo.OutboundQueue.Writer.TryWrite(new GameAgentMessage
            {
                CloseRoom = new CloseRoom
                {
                    RoomId = matchId,
                    Reason = reason,
                    Redirects = { redirects },
                },
            });
        }
    }

    /// <summary>매칭 서버에 "접속 중인 로비 아무거나 하나"를 요청한다.</summary>
    private async Task<(string ServerId, string Address)?> RequestLobbyAddressAsync()
    {
        if (string.IsNullOrEmpty(_masterConnection.MatchingServerAddress))
        {
            _logger.LogWarning("매칭 서버 주소 없음 - 로비 주소 요청 실패");
            return null;
        }

        try
        {
            using var channel = GrpcChannel.ForAddress(_masterConnection.MatchingServerAddress);
            var client = new IngameMatchControl.IngameMatchControlClient(channel);
            var response = await client.RequestLobbyAddressAsync(new RequestLobbyAddressRequest());

            return response.Found ? (response.LobbyServerId, response.Address) : null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "로비 주소 요청 실패");
            return null;
        }
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

                    case GameServerMessage.PayloadOneofCase.RoomTimeout:
                        // 게임 서버가 자체적으로 결원을 감지한 것 - CancelRoom과 같은 처리를
                        // 그대로 재사용한다(수신 루프를 안 막게 fire-and-forget).
                        _logger.LogWarning("게임 서버가 방-채움 타임아웃 보고: {RoomId}", message.RoomTimeout.RoomId);
                        _ = CloseRoomAndRedirectAsync(message.RoomTimeout.RoomId, "방-채움 타임아웃 (결원)");
                        break;

                    case GameServerMessage.PayloadOneofCase.FriendCommand:
                        _ = HandleFriendCommandAsync(message.FriendCommand, serverInfo);
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
        _clientAddress = ready.ClientAddress;

        _logger.LogInformation("게임 서버 준비 완료. Capacity={Capacity} ClientAddress={ClientAddress}",
            ready.Capacity, ready.ClientAddress);

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
        _logger.LogInformation("매치 결과 수신: MatchId={MatchId} ({Count}명)", result.MatchId, result.Players.Count);

        // 승패 판정/점수/보상 등 실제 게임 결과는 인게임 서버(진짜 권위)가 이미 정해서 보낸
        // 것이다 - 게임 에이전트는 그 판단에 관여하지 않고, "정상적으로 끝났다"는 사실만
        // 받아서 로비 복귀 처리만 담당한다("에이전트"라는 이름 그대로).
        // TODO: 매치 결과(승패, 점수 등)를 Kafka에 발행해 기록/보상 처리하는 부분은 미구현.
        //       result.Players에 담긴 순위/승패/획득 점수는 지금 아무 데도 안 쓰인다.
        await CloseRoomAndRedirectAsync(result.MatchId, "매치 정상 종료");
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
        else if (!TokenVerification.IsValid(session, request.Token))
        {
            // 로비의 EnterLobby와 완전히 같은 판단 기준(현재/직전 토큰 대조) - 검증 로직이
            // 갈라지지 않도록 Shared.Common.Auth의 공용 함수를 그대로 재사용한다.
            response.Approved = false;
            response.Reason = "INVALID_TOKEN";
            _logger.LogWarning("접속 거부 - 토큰 불일치: PlayerId={PlayerId}", request.PlayerId);
        }
        else
        {
            // 권한 이전 요청 - 기존 서버(로비)가 죽었거나 이미 이전됐으면 승인.
            // "갱신 권한은 OwnerServerId를 가진 서버만" 원칙에 따라, 권한을 넘겨받는
            // 이 시점에 새 토큰도 같이 발급/회전한다 - 로비의 EnterLobby가 하는 것과
            // 같은 패턴(클레임 호출 전에 세션 객체에 미리 반영해서 원자적으로 같이 쓰임).
            var newToken = _tokenProvider.GenerateToken(request.PlayerId);
            session.PreviousToken = session.CurrentToken;
            session.CurrentToken = newToken;
            session.TokenIssuedAtUnixMs = TimeUtils.CurrentTimestamp();

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
                response.NewToken = newToken;
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

    /// <summary>
    /// 인게임 중 클라의 친구 관리(조회/추가/삭제) 요청을 릴레이받아 처리한다. 로비의
    /// GetFriends/AddFriend/RemoveFriend와 완전히 같은 원칙(친구 관계 판단은 항상
    /// AccountServer가 실제 데이터를 갖고 있고, 게임 에이전트는 그 대리 호출만 담당) -
    /// request.PlayerId는 인게임 서버가 ConnectionApproval 때 이미 검증해둔 값으로,
    /// 클라 자체 신고를 신뢰하지 않는다(ingame.proto의 FriendCommandRequest 문서 참고).
    /// </summary>
    private async Task HandleFriendCommandAsync(FriendCommandRequest request, GameServerInfo serverInfo)
    {
        if (string.IsNullOrEmpty(_masterConnection.AccountServerAddress))
        {
            _logger.LogWarning("계정 서버 주소 없음 - 친구 명령어 처리 불가: {PlayerId}", request.PlayerId);
            return;
        }

        var response = new FriendCommandResponse { ClientId = request.ClientId };

        try
        {
            using var channel = CreateAccountServerChannel(_masterConnection.AccountServerAddress);
            var client = new AccountServer.AccountControl.AccountControlClient(channel);

            switch (request.CommandCase)
            {
                case FriendCommandRequest.CommandOneofCase.GetFriends:
                    var getResponse = await client.GetFriendsAsync(
                        new AccountServer.GetFriendsRequest { PlayerId = request.PlayerId });
                    var getFriendsResult = new GetFriendsResult();
                    getFriendsResult.FriendPlayerIds.AddRange(getResponse.FriendPlayerIds);
                    response.GetFriends = getFriendsResult;
                    break;

                case FriendCommandRequest.CommandOneofCase.AddFriend:
                    var addResponse = await client.AddFriendAsync(new AccountServer.AddFriendRequest
                    {
                        PlayerId = request.PlayerId,
                        FriendPlayerId = request.AddFriend.FriendPlayerId,
                    });
                    response.AddFriend = new AddFriendResult
                    {
                        Success = addResponse.Success,
                        ErrorMessage = addResponse.ErrorMessage,
                    };
                    break;

                case FriendCommandRequest.CommandOneofCase.RemoveFriend:
                    var removeResponse = await client.RemoveFriendAsync(new AccountServer.RemoveFriendRequest
                    {
                        PlayerId = request.PlayerId,
                        FriendPlayerId = request.RemoveFriend.FriendPlayerId,
                    });
                    response.RemoveFriend = new RemoveFriendResult
                    {
                        Success = removeResponse.Success,
                        ErrorMessage = removeResponse.ErrorMessage,
                    };
                    break;

                default:
                    _logger.LogWarning("알 수 없는 FriendCommand 종류: ClientId={ClientId}", request.ClientId);
                    return;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "친구 명령어 처리 실패: PlayerId={PlayerId}", request.PlayerId);
            return;
        }

        serverInfo.OutboundQueue.Writer.TryWrite(new GameAgentMessage
        {
            FriendCommandResult = response,
        });
    }

    /// <summary>
    /// AccountServer 호출 시 SocketsHttpHandler를 명시적으로 구성한다 - bare
    /// GrpcChannel.ForAddress로 AccountServer를 호출했을 때 원인 불명의 "HTTP_1_1_REQUIRED"
    /// 오류가 재현된 적이 있어서(KNOWN_ISSUES.md 참고), 검증된 이 패턴을 그대로 재사용한다.
    /// </summary>
    private static GrpcChannel CreateAccountServerChannel(string address)
    {
        var handler = new System.Net.Http.SocketsHttpHandler
        {
            EnableMultipleHttp2Connections = true,
            KeepAlivePingDelay = TimeSpan.FromSeconds(10),
            KeepAlivePingTimeout = TimeSpan.FromSeconds(5),
            KeepAlivePingPolicy = System.Net.Http.HttpKeepAlivePingPolicy.WithActiveRequests,
        };

        return GrpcChannel.ForAddress(address, new GrpcChannelOptions
        {
            HttpHandler = handler,
            DisposeHttpClient = true,
        });
    }

    private async Task HandlePlayerJoinedAsync(PlayerJoined joined, GameServerInfo serverInfo)
    {
        serverInfo.CurrentLoad++;
        _masterConnection.UpdateStatus(serverInfo.CurrentLoad, serverInfo.Capacity);
        _logger.LogInformation("플레이어 접속: {PlayerId} ({Load}/{Capacity})",
            joined.PlayerId, serverInfo.CurrentLoad, serverInfo.Capacity);

        lock (_roomPlayersLock)
        {
            if (!_roomPlayers.TryGetValue(joined.RoomId, out var players))
            {
                players = new HashSet<string>();
                _roomPlayers[joined.RoomId] = players;
            }
            players.Add(joined.PlayerId);
        }

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

        GameServer.Common.GameType gameType;
        lock (_roomPlayersLock)
        {
            if (_roomPlayers.TryGetValue(left.RoomId, out var players))
            {
                players.Remove(left.PlayerId);
                if (players.Count == 0)
                {
                    _roomPlayers.Remove(left.RoomId);
                }
            }

            gameType = _roomGameType.TryGetValue(left.RoomId, out var gt)
                ? gt
                : GameServer.Common.GameType.Unspecified;
        }

        if (!GetAllowReconnect(gameType))
        {
            // 재접 불가능한 방 - 소유권을 즉시 놓는다. 클라가 아무 로비에나 재접속하면
            // EnterLobby가 소유자 없음을 보고 그냥 승인한다.
            var session = await _sessionRegistry.GetAsync(left.PlayerId);
            if (session != null)
            {
                session.Status = PlayerStatus.Moving;
                session.CurrentServer = string.Empty;
                session.NextServer = "lobby";
                session.OwnerServerId = string.Empty;
                await _sessionRegistry.UpdateAsync(left.PlayerId, session);
            }
            return;
        }

        // 재접 가능한 방 - 소유권은 그대로 이 게임 에이전트가 들고 있는다 (session은 안 건드림).
        // 클라가 재접속하면 EnterLobby가 "이 게임 에이전트로 가라"고 리다이렉트해준다.
        // 유예 시간 안에 재접속을 확인 못 하면(HandlePlayerJoinedAsync로 다시 안 들어오면)
        // 그때 세션을 완전히 삭제한다 (재접속 정책 - "게임 중 재접속 유예 시간 실패" 결정 반영).
        _logger.LogInformation("재접 가능한 방 - 유예 시간({Grace}) 시작: {PlayerId} (RoomId={RoomId})",
            ReconnectGracePeriod, left.PlayerId, left.RoomId);

        var playerId = left.PlayerId;
        var roomId = left.RoomId;

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(ReconnectGracePeriod);

                bool reconnected;
                lock (_roomPlayersLock)
                {
                    reconnected = _roomPlayers.TryGetValue(roomId, out var players) && players.Contains(playerId);
                }

                if (reconnected)
                {
                    _logger.LogInformation("재접속 확인됨 - 세션 유지: {PlayerId}", playerId);
                    return;
                }

                _logger.LogWarning("재접속 유예 시간 초과 - 세션 삭제: {PlayerId} (RoomId={RoomId})",
                    playerId, roomId);
                await _sessionRegistry.RemoveAsync(playerId);
                PublishSessionInvalidated(playerId);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "재접속 유예 타이머 처리 실패: {PlayerId}", playerId);
            }
        });
    }

    /// <summary>
    /// 세션이 삭제될 때 채팅 서버에 알린다 - LobbyControlService.Logout과 같은 이유/패턴.
    /// </summary>
    private void PublishSessionInvalidated(string playerId)
    {
        try
        {
            var json = System.Text.Json.JsonSerializer.Serialize(
                new SessionInvalidatedEvent { PlayerId = playerId });
            _rabbitMqProducer.PublishToQueue(SessionInvalidatedEvent.QueueName, json);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "SessionInvalidated 발행 실패: {PlayerId}", playerId);
        }
    }

    private static GameAgentMessage MakeHeartbeat() => new()
    {
        Heartbeat = new GameAgentHeartbeat(),
    };

    public GameServerInfo? GetServerInfo() => _serverInfo;
}