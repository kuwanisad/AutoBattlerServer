using Grpc.Core;
using Grpc.Net.Client;
using LobbyServer.Auth;
using LobbyServer;
using MainServer.Common.MasterConnection;
using MatchingServer;
using Microsoft.Extensions.Logging;
using Shared.Common;
using Shared.Common.Redis;
using System.Collections.Concurrent;

namespace LobbyServer.Services;

/// <summary>
/// 클라이언트가 접속하는 gRPC 서비스.
/// AuthInterceptor가 토큰 검증을 먼저 처리하고, 이 서비스는 실제 로직만 담당한다.
///
/// RequestMatch: 매칭 서버에 JoinMatch 요청 후 스트림 대기.
/// NotifyMatch: 매칭 서버가 결과를 통보하면 해당 플레이어 스트림으로 전달.
/// </summary>
public sealed class LobbyControlService : LobbyControl.LobbyControlBase
{
    private readonly ITokenProvider _tokenProvider;
    private readonly ISessionRegistry _sessionRegistry;
    private readonly MasterConnectionService _masterConnection;
    private readonly ILogger<LobbyControlService> _logger;

    // playerId → (스트림, CancellationTokenSource)
    // RequestMatch가 열린 스트림을 여기에 등록해두고 NotifyMatch가 오면 찾아서 전달한다.
    private static readonly ConcurrentDictionary<string, PendingMatch> _pendingStreams = new();

    public LobbyControlService(
        ITokenProvider tokenProvider,
        ISessionRegistry sessionRegistry,
        MasterConnectionService masterConnection,
        ILogger<LobbyControlService> logger)
    {
        _tokenProvider = tokenProvider;
        _sessionRegistry = sessionRegistry;
        _masterConnection = masterConnection;
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

        await _sessionRegistry.RegisterAsync(request.PlayerId, new PlayerSession
        {
            Status = PlayerStatus.Lobby,
            CurrentServer = context.Host,
            OwnerServerId = _masterConnection.InstanceId,
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
            OwnerServerId = _masterConnection.InstanceId,
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

        // 매칭 서버에 JoinMatch 요청
        if (!string.IsNullOrEmpty(_masterConnection.MatchingServerAddress))
        {
            using var channel = GrpcChannel.ForAddress(_masterConnection.MatchingServerAddress);
            var client = new LobbyMatchControl.LobbyMatchControlClient(channel);
            await client.JoinMatchAsync(new JoinMatchRequest
            {
                PlayerId = playerId,
                LobbyServerId = _masterConnection.InstanceId,
                SkillScore = 0, // TODO: 실력 점수 연동
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

        // 스트림 등록 후 결과 대기
        var cts = CancellationTokenSource.CreateLinkedTokenSource(context.CancellationToken);
        var pending = new PendingMatch { Stream = responseStream, Cts = cts };
        _pendingStreams[playerId] = pending;

        try
        {
            await Task.Delay(Timeout.Infinite, cts.Token);
        }
        catch (OperationCanceledException) { }
        finally
        {
            _pendingStreams.TryRemove(playerId, out _);
        }
    }

    public override async Task<CancelMatchResponse> CancelMatch(CancelMatchRequest request, ServerCallContext context)
    {
        var playerId = context.UserState["playerId"] as string;
        _logger.LogInformation("매치메이킹 취소: {PlayerId}", playerId);

        if (!string.IsNullOrEmpty(playerId))
        {
            // 매칭 서버에 LeaveMatch 요청
            if (!string.IsNullOrEmpty(_masterConnection.MatchingServerAddress))
            {
                using var channel = GrpcChannel.ForAddress(_masterConnection.MatchingServerAddress);
                var client = new LobbyMatchControl.LobbyMatchControlClient(channel);
                await client.LeaveMatchAsync(new LeaveMatchRequest { PlayerId = playerId });
            }

            // 세션 Lobby로 복원
            var session = await _sessionRegistry.GetAsync(playerId);
            if (session != null)
            {
                session.Status = PlayerStatus.Lobby;
                session.NextServer = string.Empty;
                await _sessionRegistry.UpdateAsync(playerId, session);
            }

            // 대기 중인 스트림 취소
            if (_pendingStreams.TryRemove(playerId, out var pending))
                pending.Cts.Cancel();
        }

        return new CancelMatchResponse { Success = true };
    }

    public override async Task<MatchNotificationResponse> NotifyMatch(
        MatchNotification request, ServerCallContext context)
    {
        string playerId;
        bool isConfirmed;

        switch (request.ResultCase)
        {
            case MatchNotification.ResultOneofCase.Confirmed:
                playerId = request.Confirmed.PlayerId;
                isConfirmed = true;
                break;
            case MatchNotification.ResultOneofCase.Cancelled:
                playerId = request.Cancelled.PlayerId;
                isConfirmed = false;
                break;
            default:
                return new MatchNotificationResponse { Success = false };
        }

        if (!_pendingStreams.TryGetValue(playerId, out var pending))
        {
            _logger.LogWarning("NotifyMatch - 대기 중인 스트림 없음: {PlayerId}", playerId);
            return new MatchNotificationResponse { Success = false };
        }

        if (isConfirmed)
        {
            var confirmed = request.Confirmed;

            // 세션 Moving으로 업데이트 + OwnerServerId를 인게임 서버로 이전
            var session = await _sessionRegistry.GetAsync(playerId);
            if (session != null)
            {
                session.Status = PlayerStatus.Moving;
                session.NextServer = confirmed.IngameAddress;
                session.OwnerServerId = confirmed.IngameServerId;
                await _sessionRegistry.UpdateAsync(playerId, session);
            }

            await pending.Stream.WriteAsync(new MatchEvent
            {
                MatchFound = new MatchFound
                {
                    MatchId = Guid.NewGuid().ToString("N"),
                    GameServerAddress = confirmed.IngameAddress,
                    IngameServerId = confirmed.IngameServerId,
                    TimestampUnixMs = TimeUtils.CurrentTimestamp(),
                },
            });

            _logger.LogInformation("매치 확정 전달: {PlayerId} → {Server}",
                playerId, confirmed.IngameAddress);
        }
        else
        {
            var session = await _sessionRegistry.GetAsync(playerId);
            if (session != null)
            {
                session.Status = PlayerStatus.Lobby;
                session.NextServer = string.Empty;
                await _sessionRegistry.UpdateAsync(playerId, session);
            }

            await pending.Stream.WriteAsync(new MatchEvent
            {
                MatchFailed = new MatchFailed { Reason = request.Cancelled.Reason },
            });

            _logger.LogInformation("매칭 취소 전달: {PlayerId} ({Reason})",
                playerId, request.Cancelled.Reason);
        }

        // 스트림 종료
        pending.Cts.Cancel();

        return new MatchNotificationResponse { Success = true };
    }

    private sealed class PendingMatch
    {
        public IServerStreamWriter<MatchEvent> Stream { get; set; } = null!;
        public CancellationTokenSource Cts { get; set; } = null!;
    }
}