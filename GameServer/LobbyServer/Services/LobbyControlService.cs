using GameServer.Common;
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
    private readonly GlobalWorker _globalWorker;
    private readonly LobbyAccountServerIntermediary _accountServer;
    private readonly LobbyMailDeliverer _mailDeliverer;
    private readonly LobbyMainServerIntermediary _mainServerIntermediary;
    private readonly ISessionRegistry _sessionRegistry;
    private readonly IRateLimiter _rateLimiter;
    private readonly MasterConnectionService _masterConnection;
    private readonly MatchNotificationDispatcher _notificationDispatcher;
    private readonly RabbitMqBroker _rabbitMq;
    private readonly ILogger<LobbyControlService> _logger;

    public LobbyControlService(
        ITokenProvider tokenProvider,
        LobbyConnectionRegistry connectionRegistry,
        GlobalWorker globalWorker,
        LobbyAccountServerIntermediary accountServer,
        LobbyMailDeliverer mailDeliverer,
        LobbyMainServerIntermediary mainServerIntermediary,
        ISessionRegistry sessionRegistry,
        IRateLimiter rateLimiter,
        MasterConnectionService masterConnection,
        MatchNotificationDispatcher notificationDispatcher,
        RabbitMqBroker rabbitMq,
        ILogger<LobbyControlService> logger)
    {
        _tokenProvider = tokenProvider;
        _connectionRegistry = connectionRegistry;
        _globalWorker = globalWorker;
        _accountServer = accountServer;
        _mailDeliverer = mailDeliverer;
        _mainServerIntermediary = mainServerIntermediary;
        _sessionRegistry = sessionRegistry;
        _rateLimiter = rateLimiter;
        _masterConnection = masterConnection;
        _notificationDispatcher = notificationDispatcher;
        _rabbitMq = rabbitMq;
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
    /// RabbitMqBroker는 버퍼에 넣고 바로 돌아온다 - RabbitMQ가 끊겨 있어도 쌓아뒀다가 접속되면 보낸다.
    /// 여기서 잡는 예외는 직렬화 같은 로컬 오류뿐이고, 로그만 남기고 삼킨다(세션 삭제 자체는 이미 끝남).
    /// </summary>
    private void PublishSessionInvalidated(string playerId)
    {
        try
        {
            var json = System.Text.Json.JsonSerializer.Serialize(
                new SessionInvalidatedEvent { PlayerId = playerId });
            _rabbitMq.PublishToQueue(SessionInvalidatedEvent.QueueName, json);
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
            return new EnterLobbyResponse { Result = EnterLobbyResult.MissingCredentials };
        }

        // 남용 방지 - 이 시간 안에 실제 세션 정리가 끝나는지와는 무관하다(별개 관심사).
        // 그냥 짧은 쿨다운으로 같은 플레이어의 과도한 반복 호출만 막는다.
        var allowed = await _rateLimiter.TryAcquireAsync($"ratelimit:enterlobby:{playerId}", EnterLobbyRateLimitWindow);
        if (!allowed)
        {
            return new EnterLobbyResponse { Result = EnterLobbyResult.RateLimited };
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
            return new EnterLobbyResponse { Result = EnterLobbyResult.InvalidToken };
        }

        // 인게임에서 돌아오는 전환이면 일회용 임시 티켓도 검증한다 - 훔친 영구 토큰으로
        // 진짜 클라보다 먼저 도착하는 걸 막기 위함(KNOWN_ISSUES.md 참고). 판단 기준은 세션
        // 상태다: 이동 중(Moving)이고 그 이동 대상이 이 로비(OwnerServerId)면 반드시 티켓을
        // 소비해야 하고, 티켓이 TTL(10초)로 이미 지워졌으면 거부한다. 로그인 서버발 최초 진입
        // (Status=Lobby, 소유자=로그인 서버)이나 재접속 불가 방에서 끊겨 소유자를 비운 세션은
        // 대상이 아니다.
        //
        // 예전엔 HasPendingTicket(티켓이 아직 있는가)으로 판단해서, 티켓 만료 뒤에 온 클라는
        // 검증 없이 통과했다.
        //
        // 원래는 existing.OwnerServerType == Ingame으로 구분했었는데, 게임 에이전트의
        // CloseRoomAndRedirectAsync가 리다이렉트 준비 시점에 OwnerServerType을 이미
        // Lobby로 미리 바꿔버려서, 클라가 실제로 도착할 땐 이 조건이 항상 거짓이 되어
        // 티켓 검증 자체가 통째로 스킵되고 있었다 - 게임 에이전트 쪽(GameServerHandler)
        // 승인 로직에서 같은 패턴의 버그를 먼저 발견해서 고치면서 여기도 같이 확인함
        // (KNOWN_ISSUES.md 참고).
        if (existing != null &&
            existing.Status == PlayerStatus.Moving &&
            existing.OwnerServerId == _masterConnection.InstanceId)
        {
            if (!_mainServerIntermediary.TryConsume(playerId, request.TransferTicket))
            {
                _logger.LogWarning("전입 티켓 불일치/만료로 로비 입장 거부: {PlayerId}", playerId);
                return new EnterLobbyResponse { Result = EnterLobbyResult.InvalidTransferTicket };
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
            return new EnterLobbyResponse { Result = EnterLobbyResult.Ok, Token = newToken };
        }

        _logger.LogInformation("로비 입장 거부 - 다른 서버가 아직 소유 중: {PlayerId} @ {Address}",
            playerId, result.PreviousOwner?.Address);

        return new EnterLobbyResponse
        {
            Result = EnterLobbyResult.SessionConflict,
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

        // 접속 등록과 "지금 활성화된 지속 업무(공지 등) 받기"를 한 락 안에서 원자적으로
        // 처리한다 - 안 그러면 공지 활성화 시점의 전체 스냅샷과 겹쳐서, 그 찰나에
        // 접속한 클라가 공지를 영영 못 받는 경합이 생긴다(GlobalWorker 주석 참고).
        lock (_connectionRegistry.SyncRoot)
        {
            _connectionRegistry.Register(playerId, channel);
            _globalWorker.AddWork(playerId);
        }

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

                // 매칭 중이었는지는 이제 하드코딩된 가정이 아니라 실제 상태로 판단한다 -
                // RequestMatch가 대기 시작 시점에 Tasks[Matching]=Pending을 남겨두고,
                // 결과가 나오든 끊기든 자기 스트림이 끝날 때 지운다. StayConnected 쪽
                // 감지(이 메서드)가 RequestMatch 자신의 감지보다 먼저 도착하는 경우가
                // 있어서(실제로 확인됨 - RequestMatch 쪽 감지가 안 되거나 훨씬 느렸음,
                // KNOWN_ISSUES.md 참고) 여기서도 확인해서 필요하면 먼저 정리해준다.
                if (_connectionRegistry.TryGetClientInfo(playerId, out var clientInfo) &&
                    clientInfo.Tasks.TryGetValue(RequestedClientTask.Matching, out var matchingState) &&
                    matchingState == RequestedClientTaskState.Pending)
                {
                    await RequestLeaveMatchAsync(playerId);
                }
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
                MatchFailed = new MatchFailed { Reason = MatchFailReason.PlayerIdMissing },
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
                MatchFailed = new MatchFailed { Reason = MatchFailReason.NotOwnedByThisServer },
            });
            return;
        }

        // Status는 계속 Lobby로 둔다 - 예전엔 여기서 Matching으로 바꿨는데, 그 상태를
        // 검사하는 곳이 없었고 오히려 매칭 대기 중 끊긴 세션이 정리 안 되는 리크만
        // 만들었다(위 enum 정의 주석, KNOWN_ISSUES.md 참고).

        // 매칭 서버에 JoinMatch 요청 (단항 - 요청/응답이 필요하므로 스트림으로 안 옮김).
        // 예전엔 결과를 안 보고 무조건 "대기열 진입"을 보냈다 - 그러면 매칭 서버가 거부했거나(지원 안
        // 하는 GameType 등) 호출 자체가 실패해도, 대기열에 없으니 타임아웃도 안 걸려서 클라가 영원히
        // 기다렸다. 이제 실패하면 그 사유로 MatchFailed를 보내고 끝낸다.
        var joinFailure = await TryJoinMatchAsync(playerId, request.GameType);
        if (joinFailure != null)
        {
            await responseStream.WriteAsync(new MatchEvent { MatchFailed = joinFailure });
            return;
        }

        // 대기열 진입 알림
        await responseStream.WriteAsync(new MatchEvent
        {
            Queued = new MatchQueued { QueuePosition = 1, EstimatedWaitMs = 0 },
        });

        // 지금부터 매칭 대기 중이라는 걸 기록해둔다 - StayConnected 연결이 끊겼을 때
        // "정말 매칭 중이었는지"를 이걸로 정확히 판단한다(예전엔 무조건 LeaveMatch를
        // 불렀음 - 무해하지만 부정확했다, KNOWN_ISSUES.md 참고).
        if (_connectionRegistry.TryGetClientInfo(playerId, out var clientInfo))
        {
            clientInfo.Tasks[RequestedClientTask.Matching] = RequestedClientTaskState.Pending;
        }

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

            // 이 스트림이 어떻게 끝났든(매치 확정/취소/클라 연결 끊김) 더 이상 "대기 중"은
            // 아니므로 지운다. StayConnected 쪽이 이미 먼저 감지해서 LeaveMatch까지
            // 부른 뒤일 수도 있는데, 그래도 여기서 지우는 건 안전하다(중복 호출 방지 목적).
            if (_connectionRegistry.TryGetClientInfo(playerId, out var info))
            {
                info.Tasks.TryRemove(RequestedClientTask.Matching, out _);
            }

            // context.CancellationToken이 취소됐다는 건 클라 연결 자체가 끊겼다는 뜻이다
            // (매치 결과가 도착해서 취소된 거면 MatchNotificationDispatcher가 자기 cts만
            // 따로 취소하지, 클라의 링크된 토큰까지 취소시키진 않는다).
            // 이 경우에만 매칭 서버에서도 빼달라고 요청한다 - 이미 매치 결과로 빠진 경우
            // 또 부르면 매칭 서버 쪽에서 "대기 중이 아닌 플레이어" 경고만 찍히고 실익이 없다.
            // (StayConnected 쪽 감지가 이미 먼저 처리했을 수도 있어 중복될 수 있는데,
            // LeaveMatch는 중복 호출에 안전하다 - 두 경로 중 뭐가 먼저 감지하든 상관없게
            // 방어적으로 둘 다 남겨둔다.)
            if (context.CancellationToken.IsCancellationRequested)
            {
                _logger.LogInformation("매칭 중 클라 접속 끊김 - LeaveMatch 요청: {PlayerId}", playerId);
                await RequestLeaveMatchAsync(playerId);
            }
        }
    }

    /// <summary>매칭 서버 대기열에 넣는다. 성공하면 null, 실패하면 클라에 보낼 MatchFailed.</summary>
    private async Task<MatchFailed?> TryJoinMatchAsync(string playerId, GameServer.Common.GameType gameType)
    {
        if (string.IsNullOrEmpty(_masterConnection.MatchingServerAddress))
        {
            _logger.LogWarning("매칭 서버 주소 없음 - 매칭 서버가 연결됐는지 확인하세요. (Player={PlayerId})", playerId);
            return new MatchFailed { Reason = MatchFailReason.MatchingServerUnavailable, Detail = "matching server address unknown" };
        }

        try
        {
            using var channel = GrpcChannel.ForAddress(_masterConnection.MatchingServerAddress);
            var client = new LobbyMatchControl.LobbyMatchControlClient(channel);
            var response = await client.JoinMatchAsync(new JoinMatchRequest
            {
                PlayerId = playerId,
                LobbyServerId = _masterConnection.InstanceId,
                SkillScore = 0, // TODO: 실력 점수 연동
                GameType = gameType,
            });

            if (response.Success) return null;

            _logger.LogWarning("매칭 대기 거부: {PlayerId} Reason={Reason} {Detail}", playerId, response.Reason, response.Detail);
            return new MatchFailed { Reason = response.Reason, Detail = response.Detail };
        }
        catch (RpcException ex)
        {
            _logger.LogWarning(ex, "JoinMatch 호출 실패: {PlayerId}", playerId);
            return new MatchFailed { Reason = MatchFailReason.MatchingServerUnavailable, Detail = ex.StatusCode.ToString() };
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
            return new AddFriendResponse { Result = FriendResult.MissingCredentials };
        }

        try
        {
            var response = await _accountServer.AddFriendAsync(playerId, request.FriendPlayerId);
            // 계정 서버의 결과를 그대로 전달한다(같은 FriendResult enum - common.proto).
            return new AddFriendResponse { Result = response.Result, Detail = response.Detail };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "AddFriend 요청 실패: {PlayerId}", playerId);
            return new AddFriendResponse { Result = FriendResult.TemporaryFailure, Detail = ex.GetType().Name };
        }
    }

    public override async Task<RemoveFriendResponse> RemoveFriend(RemoveFriendRequest request, ServerCallContext context)
    {
        var playerId = context.UserState["playerId"] as string;
        if (string.IsNullOrEmpty(playerId))
        {
            return new RemoveFriendResponse { Result = FriendResult.MissingCredentials };
        }

        try
        {
            var response = await _accountServer.RemoveFriendAsync(playerId, request.FriendPlayerId);

            // 채팅 서버 통보는 AccountServer가 직접 처리한다
            // (RabbitMqBroker → RabbitMQ → ChatServer.FriendRemovedSubscriber) -
            // 로비는 여기서 그 흐름에 관여하지 않는다. 결과는 그대로 전달(같은 FriendResult enum).
            return new RemoveFriendResponse { Result = response.Result, Detail = response.Detail };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "RemoveFriend 요청 실패: {PlayerId}", playerId);
            return new RemoveFriendResponse { Result = FriendResult.TemporaryFailure, Detail = ex.GetType().Name };
        }
    }
}