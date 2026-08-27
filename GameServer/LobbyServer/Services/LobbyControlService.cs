using Grpc.Core;
using Grpc.Net.Client;
using LobbyServer.Auth;
using LobbyServer;
using MainServer.Common.MasterConnection;
using MatchingServer;
using Microsoft.Extensions.Logging;
using Shared.Common;
using Shared.Common.Redis;

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
    private readonly ISessionRegistry _sessionRegistry;
    private readonly IRateLimiter _rateLimiter;
    private readonly MasterConnectionService _masterConnection;
    private readonly MatchNotificationDispatcher _notificationDispatcher;
    private readonly ILogger<LobbyControlService> _logger;

    public LobbyControlService(
        ITokenProvider tokenProvider,
        ISessionRegistry sessionRegistry,
        IRateLimiter rateLimiter,
        MasterConnectionService masterConnection,
        MatchNotificationDispatcher notificationDispatcher,
        ILogger<LobbyControlService> logger)
    {
        _tokenProvider = tokenProvider;
        _sessionRegistry = sessionRegistry;
        _rateLimiter = rateLimiter;
        _masterConnection = masterConnection;
        _notificationDispatcher = notificationDispatcher;
        _logger = logger;
    }

    public override async Task<LogoutResponse> Logout(LogoutRequest request, ServerCallContext context)
    {
        var playerId = context.UserState["playerId"] as string;
        _logger.LogInformation("로그아웃: {PlayerId}", playerId);

        if (!string.IsNullOrEmpty(playerId))
            await _sessionRegistry.RemoveAsync(playerId);

        return new LogoutResponse { Success = true };
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
        var newToken = _tokenProvider.GenerateToken(playerId);

        var newSession = new PlayerSession
        {
            Status = PlayerStatus.Lobby,
            CurrentServer = context.Host,
            CurrentToken = newToken,
            PreviousToken = existing?.CurrentToken ?? string.Empty,
            TokenIssuedAtUnixMs = TimeUtils.CurrentTimestamp(),
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

    public override async Task RequestMatch(
        MatchRequest request,
        IServerStreamWriter<MatchEvent> responseStream,
        ServerCallContext context)
    {
        var playerId = context.UserState["playerId"] as string;
        if (string.IsNullOrEmpty(playerId))
        {
            await responseStream.WriteAsync(new MatchEvent
            {
                MatchFailed = new MatchFailed { Reason = "플레이어 ID 없음" },
            });
            return;
        }

        _logger.LogInformation("매치메이킹 요청: {PlayerId}", playerId);

        // 세션 Matching으로 업데이트
        var session = await _sessionRegistry.GetAsync(playerId);
        if (session != null)
        {
            session.Status = PlayerStatus.Matching;
            await _sessionRegistry.UpdateAsync(playerId, session);
        }

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
        _logger.LogInformation("매치메이킹 취소: {PlayerId}", playerId);

        if (!string.IsNullOrEmpty(playerId))
        {
            await RequestLeaveMatchAsync(playerId);

            // 세션 Lobby로 복원
            var session = await _sessionRegistry.GetAsync(playerId);
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
}