using Grpc.Core;
using Grpc.Net.Client;
using LobbyServer.Auth;
using LobbyServer;
using MainServer.Common.MasterConnection;
using MainServer.Common.Messaging;
using MatchingServer;
using Microsoft.Extensions.Logging;
using Shared.Common;
using Shared.Common.Auth;
using Shared.Common.Messaging;
using Shared.Common.Redis;
using System.Collections.Concurrent;

namespace LobbyServer.Services;

/// <summary>
/// 클라이언트가 접속하는 gRPC 서비스.
/// AuthInterceptor가 토큰 검증을 먼저 처리하고, 이 서비스는 실제 로직만 담당한다.
///
/// RequestMatch: 매칭 서버에 JoinMatch 요청 후 스트림 대기.
/// 매칭 결과 통보는 MatchNotificationDispatcher(매칭 서버와의 상시 접속 스트림 수신부)가
/// 처리해서 그 스트림으로 전달한다 - 여기서는 스트림을 등록/해제만 한다.
/// </summary>
public sealed class LobbyControlService : LobbyControl.LobbyControlBase
{
    // EnterLobby 남용 방지 쿨다운. 세션 반영 시간이랑 맞출 필요 없음 - 그냥 짧은 값.
    private static readonly TimeSpan EnterLobbyRateLimitWindow = TimeSpan.FromMilliseconds(300);

    private readonly ITokenProvider _tokenProvider;
    private readonly LobbyConnectionRegistry _connectionRegistry;
    private readonly LobbyAccountServerIntermediary _accountServer;
    private readonly LobbyMailDeliverer _mailDeliverer;
    private readonly LobbyMainServerIntermediary _mainServerIntermediary;
    private readonly ISessionRegistry _sessionRegistry;
    private readonly IRateLimiter _rateLimiter;
    private readonly MasterConnectionService _masterConnection;
    private readonly MatchNotificationDispatcher _notificationDispatcher;
    private readonly RabbitMqProducer _rabbitMqProducer;
    private readonly ILogger<LobbyControlService> _logger;

    public LobbyControlService(
        ITokenProvider tokenProvider,
        LobbyConnectionRegistry connectionRegistry,
        LobbyAccountServerIntermediary accountServer,
        LobbyMailDeliverer mailDeliverer,
        LobbyMainServerIntermediary mainServerIntermediary,
        ISessionRegistry sessionRegistry,
        IRateLimiter rateLimiter,
        MasterConnectionService masterConnection,
        MatchNotificationDispatcher notificationDispatcher,
        RabbitMqProducer rabbitMqProducer,
        ILogger<LobbyControlService> logger)
    {
        _tokenProvider = tokenProvider;
        _connectionRegistry = connectionRegistry;
        _accountServer = accountServer;
        _mailDeliverer = mailDeliverer;
        _mainServerIntermediary = mainServerIntermediary;
        _sessionRegistry = sessionRegistry;
        _rateLimiter = rateLimiter;
        _masterConnection = masterConnection;
        _notificationDispatcher = notificationDispatcher;
        _rabbitMqProducer = rabbitMqProducer;
        _logger = logger;
    }

    public override async Task<LogoutResponse> Logout(LogoutRequest request, ServerCallContext context)
    {
        var playerId = context.UserState["playerId"] as string;
        var submittedToken = context.UserState["token"] as string ?? string.Empty;
        _logger.LogInformation("로그아웃: {PlayerId}", playerId);

        if (!string.IsNullOrEmpty(playerId))
        {
            // 이 로비가 실제 소유자일 때만 지운다 - 아니면 다른 로비가 소유한 세션을
            // 엉뚱한 로비가 지워버리는 사고가 생길 수 있다(KNOWN_ISSUES.md 참고).
            var session = await GetOwnedSessionOrNullAsync(playerId, submittedToken);
            if (session != null)
            {
                await _sessionRegistry.RemoveAsync(playerId);
                PublishSessionInvalidated(playerId);
            }
        }

        return new LogoutResponse { Success = true };
    }

    /// <summary>
    /// 세션이 삭제될 때 채팅 서버에 알린다 - "서비스 제공 가능 여부는 전적으로 메인
    /// 서버한테 맡긴다"는 원칙에 따라, 채팅 서버는 이 신호로만 연결 종료를 판단한다.
    /// 발행 실패는 로그만 남기고 삼킨다 - 세션 삭제 자체(이미 끝남)를 막을 이유는 아니다.
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

    /// <summary>
    /// 클라가 접속 직후(또는 다른 서버에서 넘어온 직후) 호출한다. 이 로비가 플레이어
    /// 관리 권한을 가져올 수 있는지 확인하고, 못 가져오면(다른 서버가 아직 살아서
    /// 소유 중이면) 그 서버 주소를 돌려준다. 성공하면 새 토큰을 발급해서 돌려준다 -
    /// 클라가 다음 서버 이동 때 EnterLobby를 다시 부를 때 이 토큰을 쓴다.
    ///
    /// 3번 결정: Login/CreateAccount가 코드에서 빠졌으므로, 여기가 사실상 "신원 확인 후
    /// 토큰을 발급하는" 유일한 지점이 됐다. 지금은 DummyTokenProvider가 뭐가 와도
    /// 통과시키므로 실질적인 신원 검증은 없는 임시 상태 - 로그인 서버가 생기기 전까지.
    /// 토큰 회전(1번 결정): 세션 레지스트리에 CurrentToken/PreviousToken 2슬롯을 두고,
    /// 성공할 때마다 CurrentToken을 PreviousToken으로 밀어내고 새로 발급한다. 실제 제출된
    /// 토큰이 유효한지 여기서 대조/거부하는 로직은 아직 없다(TODO - 재접속 정책과 함께
    /// ConnectionApproval 쪽과 짝을 맞춰 나중에 구현).
    /// </summary>
    public override async Task<EnterLobbyResponse> EnterLobby(EnterLobbyRequest request, ServerCallContext context)
    {
        var playerId = context.UserState["playerId"] as string;
        var submittedToken = context.UserState["token"] as string ?? string.Empty;
        if (string.IsNullOrEmpty(playerId))
        {
            return new EnterLobbyResponse { Success = false, ErrorMessage = "인증 정보 없음" };
        }

        // 남용 방지 - 이 시간 안에 실제 세션 정리가 끝나는지와는 무관하다(별개 관심사).
        // 그냥 짧은 쿨다운으로 같은 플레이어의 과도한 반복 호출만 막는다.
        var allowed = await _rateLimiter.TryAcquireAsync($"ratelimit:enterlobby:{playerId}", EnterLobbyRateLimitWindow);
        if (!allowed)
        {
            return new EnterLobbyResponse { Success = false, RateLimited = true };
        }

        var existing = await _sessionRegistry.GetAsync(playerId);

        // 제출된 토큰이 세션의 현재/직전 토큰과 일치하는지 확인 (2슬롯 - 모바일 환경에서
        // 회전 직후 접속이 끊겨 클라가 직전 세대 토큰을 들고 재접속하는 경우까지 허용).
        // 로그인 서버가 막 이관해준 직후의 최초 진입도, 로그인 서버가 세션에 심어둔
        // CurrentToken과 클라가 받은 토큰이 일치하므로 이 검증 하나로 자연스럽게 통과한다 -
        // "최초 진입"과 "재접속"을 별도 분기로 나눌 필요가 없다.
        if (!TokenVerification.IsValid(existing, submittedToken))
        {
            _logger.LogWarning("토큰 불일치로 로비 입장 거부: {PlayerId}", playerId);
            return new EnterLobbyResponse { Success = false, ErrorMessage = "INVALID_TOKEN" };
        }

        // 인게임에서 돌아오는 전환이면 일회용 임시 티켓도 검증한다 - 훔친 영구 토큰으로
        // 진짜 클라보다 먼저 도착하는 걸 막기 위함(KNOWN_ISSUES.md 참고). 로그인 서버발
        // 최초 진입 등 다른 경로는 애초에 이 로비한테 미리 등록된 티켓이 없으므로
        // 대상이 아니다 - existing.OwnerServerType으로 구분한다(단순히 필드를 비워서
        // 우회할 수 없음 - Ingame 소유였으면 반드시 TryConsume을 통과해야 함).
        if (existing != null && existing.OwnerServerType == GameServer.Common.ServerType.Ingame)
        {
            if (!_mainServerIntermediary.TryConsume(playerId, request.TransferTicket))
            {
                _logger.LogWarning("전입 티켓 불일치/만료로 로비 입장 거부: {PlayerId}", playerId);
                return new EnterLobbyResponse { Success = false, ErrorMessage = "INVALID_TRANSFER_TICKET" };
            }
        }

        var newToken = _tokenProvider.GenerateToken(playerId);

        var newSession = new PlayerSession
        {
            Status = PlayerStatus.Lobby,
            CurrentServer = context.Host,
            CurrentToken = newToken,
            PreviousToken = existing?.CurrentToken ?? string.Empty,
            TokenIssuedAtUnixMs = TimeUtils.CurrentTimestamp(),
            OwnerServerType = GameServer.Common.ServerType.Lobby,
        };

        var result = await _sessionRegistry.TryClaimOwnershipWithPreviousOwnerAsync(
            _masterConnection.InstanceId, playerId, newSession);

        if (result.Claimed)
        {
            _logger.LogInformation("로비 입장: {PlayerId}", playerId);
            return new EnterLobbyResponse { Success = true, Token = newToken };
        }

        _logger.LogInformation("로비 입장 거부 - 다른 서버가 아직 소유 중: {PlayerId} @ {Address}",
            playerId, result.PreviousOwner?.Address);

        return new EnterLobbyResponse
        {
            Success = false,
            HasConflictingSession = true,
            ConflictingServerAddress = result.PreviousOwner?.Address ?? string.Empty,
        };
    }

    /// <summary>
    /// EnterLobby 성공 직후 클라가 연다. 스트림이 살아있다는 사실 자체가 "클라가 아직
    /// 로비에 있다"는 증거이자, 이 스트림을 통해 우편함 갱신 등을 push하는 통로이기도
    /// 하다. 클라가 끊거나(와이파이 등) 서버가 종료될 때까지 유지된다.
    ///
    /// 실제 쓰기(WriteAsync)는 이 메서드 하나만 담당한다 - NotifyMailUpdated 등 다른
    /// 요청 핸들러가 같은 스트림에 직접 쓰면 동시 쓰기로 깨질 수 있어서, 그쪽은 이
    /// 연결 전용 채널(Channel)에 넣기만 하고, 여기서 그 채널을 읽어서 순차적으로
    /// 스트림에 쓴다(게임 에이전트의 OutboundQueue와 같은 패턴).
    /// </summary>
    public override async Task StayConnected(
        StayConnectedRequest request,
        IServerStreamWriter<StayConnectedEvent> responseStream,
        ServerCallContext context)
    {
        var playerId = context.UserState["playerId"] as string;
        if (string.IsNullOrEmpty(playerId)) return;

        var channel = System.Threading.Channels.Channel.CreateUnbounded<StayConnectedEvent>();
        _connectionRegistry.Register(playerId, channel);

        try
        {
            // 접속 시 자동 제공(안전망) - 신호를 기다리지 않고 여기서 곧장 한 번 조회.
            // 신호(NotifyMailUpdated)가 어떤 이유로든 못 오더라도 이걸로 항상 만회된다.
            await _mailDeliverer.DeliverAsync(playerId);

            var sendLoopTask = Task.Run(async () =>
            {
                await foreach (var evt in channel.Reader.ReadAllAsync(context.CancellationToken))
                {
                    await responseStream.WriteAsync(evt);
                }
            }, context.CancellationToken);

            try
            {
                await Task.Delay(Timeout.Infinite, context.CancellationToken);
            }
            catch (OperationCanceledException)
            {
                // 정상 - 스트림 종료(클라 이탈 또는 서버 종료)
            }

            channel.Writer.TryComplete();

            try
            {
                await sendLoopTask;
            }
            catch (OperationCanceledException)
            {
                // 정상 - sendLoopTask 내부의 ReadAllAsync도 같은 context.CancellationToken을
                // 쓰기 때문에, channel.Writer.TryComplete()보다 먼저(또는 거의 동시에)
                // 취소로 끝날 수 있다. 이것도 위와 같은 "정상 종료" 경로이므로 그냥 삼킨다 -
                // 안 잡으면 gRPC 프레임워크가 매번 실패로 로그를 찍는다(실질적 피해는 없었지만
                // 시끄러운 로그가 진짜 에러랑 구분이 안 되게 만듦).
            }
        }
        finally
        {
            _connectionRegistry.Unregister(playerId);
            await HandleStayConnectedEndedAsync(playerId);
        }
    }

    /// <summary>
    /// 하위 서버(또는 다른 메인 서버)가 "이 플레이어 우편함이 갱신됐다"는 신호를 보내면,
    /// 지금 이 로비에 접속 중일 때만(연결 레지스트리에 있을 때만) 최신 목록을 다시
    /// 조회해서 전달한다. 접속 중이 아니면 그냥 success=false - 호출부 입장에서는
    /// 실패로 취급 안 해도 된다(다음 접속 시 자동 제공이 안전망).
    /// </summary>
    public override async Task<NotifyMailUpdatedResponse> NotifyMailUpdated(
        NotifyMailUpdatedRequest request, ServerCallContext context)
    {
        if (!_connectionRegistry.TryGet(request.PlayerId, out _))
        {
            return new NotifyMailUpdatedResponse { Success = false };
        }

        await _mailDeliverer.DeliverAsync(request.PlayerId);
        return new NotifyMailUpdatedResponse { Success = true };
    }

    /// <summary>클라가 능동적으로 최신 우편함을 요청할 때(새로고침 등) 직접 호출한다.</summary>
    public override async Task<MailProvided> GetMailList(GetMailListRequest request, ServerCallContext context)
    {
        var playerId = context.UserState["playerId"] as string;
        if (string.IsNullOrEmpty(playerId)) return new MailProvided();

        return await _mailDeliverer.DeliverAsync(playerId);
    }

    /// <summary>게임 에이전트가 "이 플레이어들이 곧 이 로비로 올 것"이라며 일회용 임시
    /// 티켓을 미리 등록한다 - 플레이어 토큰이 아니라 서버 간 통신이라 AuthInterceptor의
    /// ServerToServerMethods 예외 목록에 등록돼 있다.</summary>
    public override Task<RegisterIncomingTransfersResponse> RegisterIncomingTransfers(
        RegisterIncomingTransfersRequest request, ServerCallContext context)
    {
        _mainServerIntermediary.OnRegisterIncomingTransfersReceived(
            request.Tickets.Select(t => (t.PlayerId, t.Ticket)));
        return Task.FromResult(new RegisterIncomingTransfersResponse { Success = true });
    }

    /// <summary>
    /// StayConnected 스트림이 끊겼을 때, 그게 "진짜 조용히 사라진 것"인지 "정상적으로
    /// 다른 서버로 이동 중이라 끊긴 것"인지 구분한다. 매치 확정 시 MatchNotificationDispatcher가
    /// 클라에게 MatchFound를 보내기 전에 이미 OwnerServerId/Status를 갱신해두므로, 정상
    /// 이동이면 여기 도달했을 때 이미 "내 소유가 아니거나 Status가 Moving"인 상태다 -
    /// 그 좁은 race(처리 도중 우연히 끊기는 경우)까지 방어하려고 Status도 같이 확인한다.
    /// </summary>
    private async Task HandleStayConnectedEndedAsync(string playerId)
    {
        try
        {
            var session = await _sessionRegistry.GetAsync(playerId);

            if (session != null &&
                session.OwnerServerId == _masterConnection.InstanceId &&
                session.Status == PlayerStatus.Lobby)
            {
                await _sessionRegistry.RemoveAsync(playerId);
                PublishSessionInvalidated(playerId);
                _logger.LogInformation("StayConnected 스트림 종료 - 세션 정리: {PlayerId}", playerId);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "StayConnected 종료 처리 실패: {PlayerId}", playerId);
        }
    }

    public override async Task RequestMatch(
        MatchRequest request,
        IServerStreamWriter<MatchEvent> responseStream,
        ServerCallContext context)
    {
        var playerId = context.UserState["playerId"] as string;
        var submittedToken = context.UserState["token"] as string ?? string.Empty;
        if (string.IsNullOrEmpty(playerId))
        {
            await responseStream.WriteAsync(new MatchEvent
            {
                MatchFailed = new MatchFailed { Reason = "플레이어 ID 없음" },
            });
            return;
        }

        _logger.LogInformation("매치메이킹 요청: {PlayerId}", playerId);

        // 이 로비가 실제 소유자일 때만 매칭을 시작한다 - 아니면 소유권 이전 없이
        // 엉뚱한 로비가 세션을 조작할 수 있다(KNOWN_ISSUES.md 참고).
        var session = await GetOwnedSessionOrNullAsync(playerId, submittedToken);
        if (session == null)
        {
            await responseStream.WriteAsync(new MatchEvent
            {
                MatchFailed = new MatchFailed { Reason = "이 서버 소속이 아닙니다." },
            });
            return;
        }

        session.Status = PlayerStatus.Matching;
        await _sessionRegistry.UpdateAsync(playerId, session);

        // 매칭 서버에 JoinMatch 요청 (단항 - 요청/응답이 필요하므로 스트림으로 안 옮김)
        if (!string.IsNullOrEmpty(_masterConnection.MatchingServerAddress))
        {
            using var channel = GrpcChannel.ForAddress(_masterConnection.MatchingServerAddress);
            var client = new LobbyMatchControl.LobbyMatchControlClient(channel);
            await client.JoinMatchAsync(new JoinMatchRequest
            {
                PlayerId = playerId,
                LobbyServerId = _masterConnection.InstanceId,
                SkillScore = 0, // TODO: 실력 점수 연동
                GameType = request.GameType,
            });
        }
        else
        {
            _logger.LogWarning("매칭 서버 주소 없음 - 매칭 서버가 연결됐는지 확인하세요.");
        }

        // 대기열 진입 알림
        await responseStream.WriteAsync(new MatchEvent
        {
            Queued = new MatchQueued { QueuePosition = 1, EstimatedWaitMs = 0 },
        });

        // 디스패처에 등록 후 결과 대기 (결과는 MatchNotificationDispatcher가 전달)
        var cts = CancellationTokenSource.CreateLinkedTokenSource(context.CancellationToken);
        _notificationDispatcher.Register(playerId, responseStream, cts);

        try
        {
            await Task.Delay(Timeout.Infinite, cts.Token);
        }
        catch (OperationCanceledException) { }
        finally
        {
            _notificationDispatcher.TryUnregister(playerId, out _);

            // context.CancellationToken이 취소됐다는 건 클라 연결 자체가 끊겼다는 뜻이다
            // (매치 결과가 도착해서 취소된 거면 MatchNotificationDispatcher가 자기 cts만
            // 따로 취소하지, 클라의 링크된 토큰까지 취소시키진 않는다).
            // 이 경우에만 매칭 서버에서도 빼달라고 요청한다 - 이미 매치 결과로 빠진 경우
            // 또 부르면 매칭 서버 쪽에서 "대기 중이 아닌 플레이어" 경고만 찍히고 실익이 없다.
            if (context.CancellationToken.IsCancellationRequested)
            {
                _logger.LogInformation("매칭 중 클라 접속 끊김 - LeaveMatch 요청: {PlayerId}", playerId);
                await RequestLeaveMatchAsync(playerId);
            }
        }
    }

    /// <summary>
    /// 이 세션의 진짜 소유자가 지금 이 로비(자기 자신)인지 + 제출된 토큰이 지금도
    /// 유효한지(세션의 CurrentToken/PreviousToken과 대조) 둘 다 확인한다. EnterLobby/
    /// ConnectionApproval처럼 "소유권을 새로 클레임하는" 지점과 다르게, 여기서
    /// 확인하는 RPC들(RequestMatch/CancelMatch/Logout)은 "이미 소유하고 있다고
    /// 가정하고 그냥 세션을 쓰는" 지점이라, 그 가정 자체를 매번 확인해야 한다.
    ///
    /// 토큰 대조가 왜 별도로 필요한가: AuthInterceptor.ValidateToken은 토큰 문자열의
    /// 형식만 확인할 뿐 세션과 실제로 대조하지 않는다(KNOWN_ISSUES.md 참고) - 그러니
    /// 이미 회전으로 무효화됐어야 할 옛 토큰도 형식만 맞으면 AuthInterceptor를 그냥
    /// 통과한다. 소유권 확인만으로는 이 경우(진짜 소유 로비가 맞지만, 제출된 토큰
    /// 자체는 이미 낡은 경우)를 못 걸러내므로, 여기서 TokenVerification까지 같이 확인한다.
    /// </summary>
    private async Task<PlayerSession?> GetOwnedSessionOrNullAsync(string playerId, string submittedToken)
    {
        var session = await _sessionRegistry.GetAsync(playerId);
        if (session == null || session.OwnerServerId != _masterConnection.InstanceId)
        {
            return null;
        }
        if (!TokenVerification.IsValid(session, submittedToken))
        {
            return null;
        }
        return session;
    }

    /// <summary>매칭 서버에 LeaveMatch를 요청한다. CancelMatch와 접속 끊김 자동 처리 양쪽에서 쓴다.</summary>
    private async Task RequestLeaveMatchAsync(string playerId)
    {
        if (string.IsNullOrEmpty(_masterConnection.MatchingServerAddress))
        {
            _logger.LogWarning("매칭 서버 주소 없음 - LeaveMatch 요청 실패: {PlayerId}", playerId);
            return;
        }

        try
        {
            using var channel = GrpcChannel.ForAddress(_masterConnection.MatchingServerAddress);
            var client = new LobbyMatchControl.LobbyMatchControlClient(channel);
            await client.LeaveMatchAsync(new LeaveMatchRequest { PlayerId = playerId });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "LeaveMatch 요청 실패: {PlayerId}", playerId);
        }
    }

    public override async Task<CancelMatchResponse> CancelMatch(CancelMatchRequest request, ServerCallContext context)
    {
        var playerId = context.UserState["playerId"] as string;
        var submittedToken = context.UserState["token"] as string ?? string.Empty;
        _logger.LogInformation("매치메이킹 취소: {PlayerId}", playerId);

        if (!string.IsNullOrEmpty(playerId))
        {
            await RequestLeaveMatchAsync(playerId);

            // 이 로비가 실제 소유자일 때만 세션을 복원한다(KNOWN_ISSUES.md 참고).
            var session = await GetOwnedSessionOrNullAsync(playerId, submittedToken);
            if (session != null)
            {
                session.Status = PlayerStatus.Lobby;
                session.NextServer = string.Empty;
                await _sessionRegistry.UpdateAsync(playerId, session);
            }

            // 대기 중인 스트림 취소
            if (_notificationDispatcher.TryUnregister(playerId, out var cts))
                cts?.Cancel();
        }

        return new CancelMatchResponse { Success = true };
    }

    // === 친구 관리 (계정 서버 대리 호출) ===
    // 채팅 서버는 이 창구를 거치지 않는다 - "서비스 제공 가능 여부는 전적으로 메인
    // 서버한테 맡긴다"는 원칙에 따라, 친구 관계 판단은 항상 여기(또는 나중에 인게임
    // 서버의 같은 창구)를 거친다.

    public override async Task<GetFriendsResponse> GetFriends(GetFriendsRequest request, ServerCallContext context)
    {
        var playerId = context.UserState["playerId"] as string;
        if (string.IsNullOrEmpty(playerId)) return new GetFriendsResponse();

        try
        {
            var response = await _accountServer.GetFriendsAsync(playerId);
            var result = new GetFriendsResponse();
            result.FriendPlayerIds.AddRange(response.FriendPlayerIds);
            return result;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "GetFriends 요청 실패: {PlayerId}", playerId);
            return new GetFriendsResponse();
        }
    }

    public override async Task<AddFriendResponse> AddFriend(AddFriendRequest request, ServerCallContext context)
    {
        var playerId = context.UserState["playerId"] as string;
        if (string.IsNullOrEmpty(playerId))
        {
            return new AddFriendResponse { Success = false, ErrorMessage = "인증 정보 없음" };
        }

        try
        {
            var response = await _accountServer.AddFriendAsync(playerId, request.FriendPlayerId);
            return new AddFriendResponse { Success = response.Success, ErrorMessage = response.ErrorMessage };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "AddFriend 요청 실패: {PlayerId}", playerId);
            return new AddFriendResponse { Success = false, ErrorMessage = "일시적인 오류입니다." };
        }
    }

    public override async Task<RemoveFriendResponse> RemoveFriend(RemoveFriendRequest request, ServerCallContext context)
    {
        var playerId = context.UserState["playerId"] as string;
        if (string.IsNullOrEmpty(playerId))
        {
            return new RemoveFriendResponse { Success = false, ErrorMessage = "인증 정보 없음" };
        }

        try
        {
            var response = await _accountServer.RemoveFriendAsync(playerId, request.FriendPlayerId);

            // 채팅 서버 통보는 AccountServer가 트랜잭션 아웃박스로 직접 처리한다
            // (FriendEventOutbox → RabbitMQ → ChatServer.FriendRemovedSubscriber) -
            // 로비는 여기서 그 흐름에 관여하지 않는다.
            return new RemoveFriendResponse { Success = response.Success, ErrorMessage = response.ErrorMessage };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "RemoveFriend 요청 실패: {PlayerId}", playerId);
            return new RemoveFriendResponse { Success = false, ErrorMessage = "일시적인 오류입니다." };
        }
    }
}