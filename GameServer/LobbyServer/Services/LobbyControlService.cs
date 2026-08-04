using Grpc.Core;
using LobbyServer.Auth;
using LobbyServer;
using Microsoft.Extensions.Logging;
using Shared.Common.Redis;

namespace LobbyServer.Services;

/// <summary>
/// 클라이언트가 접속하는 gRPC 서비스.
/// AuthInterceptor가 토큰 검증을 먼저 처리하고, 이 서비스는 실제 로직만 담당한다.
/// MasterControlService, IngameControlService와 동일한 패턴.
/// </summary>
public sealed class LobbyControlService : LobbyControl.LobbyControlBase
{
    private readonly ITokenProvider _tokenProvider;
    private readonly ISessionRegistry _sessionRegistry;
    private readonly ILogger<LobbyControlService> _logger;

    public LobbyControlService(
        ITokenProvider tokenProvider,
        ISessionRegistry sessionRegistry,
        ILogger<LobbyControlService> logger)
    {
        _tokenProvider = tokenProvider;
        _sessionRegistry = sessionRegistry;
        _logger = logger;
    }

    public override async Task<LoginResponse> Login(LoginRequest request, ServerCallContext context)
    {
        _logger.LogInformation("로그인 요청: {PlayerId}", request.PlayerId);

        if (string.IsNullOrEmpty(request.PlayerId))
        {
            return new LoginResponse
            {
                Success = false,
                ErrorMessage = "플레이어 ID를 입력하세요.",
            };
        }

        var token = _tokenProvider.GenerateToken(request.PlayerId);

        // 로그인 시 세션 등록 (인게임에서 복귀한 경우도 여기서 Lobby로 확정)
        await _sessionRegistry.RegisterAsync(request.PlayerId, new PlayerSession
        {
            Status = PlayerStatus.Lobby,
            CurrentServer = context.Host,
        });

        _logger.LogInformation("로그인 성공: {PlayerId}", request.PlayerId);

        return new LoginResponse
        {
            Success = true,
            Token = token,
        };
    }

    public override async Task<CreateAccountResponse> CreateAccount(CreateAccountRequest request, ServerCallContext context)
    {
        _logger.LogInformation("계정 생성 요청: {PlayerId} / {Nickname}", request.PlayerId, request.Nickname);

        if (string.IsNullOrEmpty(request.PlayerId) || string.IsNullOrEmpty(request.Nickname))
        {
            return new CreateAccountResponse
            {
                Success = false,
                ErrorMessage = "플레이어 ID와 닉네임을 입력하세요.",
            };
        }

        var token = _tokenProvider.GenerateToken(request.PlayerId);

        await _sessionRegistry.RegisterAsync(request.PlayerId, new PlayerSession
        {
            Status = PlayerStatus.Lobby,
            CurrentServer = context.Host,
        });

        _logger.LogInformation("계정 생성 성공: {PlayerId}", request.PlayerId);

        return new CreateAccountResponse
        {
            Success = true,
            Token = token,
        };
    }

    public override async Task<LogoutResponse> Logout(LogoutRequest request, ServerCallContext context)
    {
        var playerId = context.UserState["playerId"] as string;
        _logger.LogInformation("로그아웃: {PlayerId}", playerId);

        if (!string.IsNullOrEmpty(playerId))
            await _sessionRegistry.RemoveAsync(playerId);

        return new LogoutResponse { Success = true };
    }

    public override async Task RequestMatch(
        MatchRequest request,
        IServerStreamWriter<MatchEvent> responseStream,
        ServerCallContext context)
    {
        var playerId = context.UserState["playerId"] as string;
        _logger.LogInformation("매치메이킹 요청: {PlayerId}", playerId);

        // 세션 상태 Matching으로 업데이트
        if (!string.IsNullOrEmpty(playerId))
        {
            var session = await _sessionRegistry.GetAsync(playerId);
            if (session != null)
            {
                session.Status = PlayerStatus.Matching;
                await _sessionRegistry.UpdateAsync(playerId, session);
            }
        }

        // 더미: 즉시 대기열 진입 → 3초 후 매치 확정
        await responseStream.WriteAsync(new MatchEvent
        {
            Queued = new MatchQueued
            {
                QueuePosition = 1,
                EstimatedWaitMs = 3000,
            },
        });

        await Task.Delay(3000, context.CancellationToken);

        // 매치 확정 전 세션을 Moving으로 업데이트
        // 이 시점부터 해커가 다른 서버로 접속 시도해도 세션 검증으로 차단 가능
        var matchToken = Guid.NewGuid().ToString("N");
        const string gameServerAddress = "127.0.0.1:7777";

        if (!string.IsNullOrEmpty(playerId))
        {
            var session = await _sessionRegistry.GetAsync(playerId);
            if (session != null)
            {
                session.Status = PlayerStatus.Moving;
                session.NextServer = gameServerAddress;
                session.MatchToken = matchToken;
                await _sessionRegistry.UpdateAsync(playerId, session);
            }
        }

        await responseStream.WriteAsync(new MatchEvent
        {
            MatchFound = new MatchFound
            {
                MatchId = Guid.NewGuid().ToString("N"),
                GameServerAddress = gameServerAddress,
                PlayerIds = { playerId },
                TimestampUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                MatchToken = matchToken,
            },
        });

        _logger.LogInformation("매치 확정: {PlayerId} → {GameServer} (MatchToken={Token})",
            playerId, gameServerAddress, matchToken);
    }

    public override async Task<CancelMatchResponse> CancelMatch(CancelMatchRequest request, ServerCallContext context)
    {
        var playerId = context.UserState["playerId"] as string;
        _logger.LogInformation("매치메이킹 취소: {PlayerId}", playerId);

        // 세션 상태 Lobby로 복원
        if (!string.IsNullOrEmpty(playerId))
        {
            var session = await _sessionRegistry.GetAsync(playerId);
            if (session != null)
            {
                session.Status = PlayerStatus.Lobby;
                session.NextServer = string.Empty;
                session.MatchToken = string.Empty;
                await _sessionRegistry.UpdateAsync(playerId, session);
            }
        }

        return new CancelMatchResponse { Success = true };
    }
}
