using Grpc.Net.Client;
using MatchingServer;
using Microsoft.Extensions.Logging;

namespace MatchingServer.Services;

/// <summary>
/// 매칭 서버가 로비 서버와 게임 에이전트에 직접 gRPC 콜백을 호출하는 클라이언트.
/// 매칭 확정/취소 통보, PrepareMatch 요청에 사용한다.
/// </summary>
public sealed class MatchCallbackClient
{
    private readonly ILogger<MatchCallbackClient> _logger;

    public MatchCallbackClient(ILogger<MatchCallbackClient> logger)
    {
        _logger = logger;
    }

    /// <summary>로비 서버에 매칭 확정을 통보한다.</summary>
    public async Task NotifyMatchConfirmedAsync(
        string lobbyAddress,
        string playerId,
        string ingameServerId,
        string ingameAddress
        )
    {
        try
        {
            using var channel = GrpcChannel.ForAddress(lobbyAddress);
            var client = new LobbyMatchControl.LobbyMatchControlClient(channel);

            await client.NotifyMatchAsync(new MatchNotification
            {
                Confirmed = new MatchConfirmed
                {
                    PlayerId = playerId,
                    IngameServerId = ingameServerId,
                    IngameAddress = ingameAddress,
                }
            });

            _logger.LogInformation("매칭 확정 통보: {PlayerId} → {IngameServerId}", playerId, ingameServerId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "매칭 확정 통보 실패: {PlayerId}", playerId);
        }
    }

    public async Task NotifyMatchCancelledAsync(
        string lobbyAddress,
        string playerId,
        string reason)
    {
        try
        {
            using var channel = GrpcChannel.ForAddress(lobbyAddress);
            var client = new LobbyMatchControl.LobbyMatchControlClient(channel);

            await client.NotifyMatchAsync(new MatchNotification
            {
                Cancelled = new MatchCancelled
                {
                    PlayerId = playerId,
                    Reason = reason,
                },
            });

            _logger.LogInformation("매칭 취소 통보: {PlayerId} ({Reason})", playerId, reason);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "매칭 취소 통보 실패: {PlayerId}", playerId);
        }
    }

    /// <summary>게임 에이전트에 매치 준비를 요청한다.</summary>
    public async Task<bool> PrepareMatchAsync(
        string ingameAddress,
        string matchId,
        List<string> playerIds)
    {
        try
        {
            using var channel = GrpcChannel.ForAddress(ingameAddress);
            var client = new IngameMatchControl.IngameMatchControlClient(channel);

            var response = await client.PrepareMatchAsync(new PrepareMatchRequest
            {
                MatchId = matchId,
                PlayerIds = { playerIds },
            });

            _logger.LogInformation("PrepareMatch 응답: {MatchId} accepted={Accepted}",
                matchId, response.Accepted);

            return response.Accepted;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "PrepareMatch 실패: {MatchId}", matchId);
            return false;
        }
    }
}