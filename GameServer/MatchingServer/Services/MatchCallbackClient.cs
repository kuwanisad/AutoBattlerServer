using Grpc.Net.Client;
using MatchingServer;
using Microsoft.Extensions.Logging;

namespace MatchingServer.Services;

/// <summary>
/// 매칭 서버가 게임 에이전트에 직접 gRPC 요청을 호출하는 클라이언트.
/// PrepareMatch(요청-응답이 필요)에만 쓴다.
///
/// 로비 서버에 대한 매칭 확정/취소 통보는 더 이상 이 클래스가 하지 않는다 -
/// 로비가 MatchConnectionControl.Connect 스트림으로 매칭 서버에 접속을 유지하고 있으므로,
/// MatchEngine이 ConnectedServerRegistry.Enqueue로 그 스트림에 바로 실어 보낸다
/// (별도 주소 조회나 새 채널을 열 필요가 없다).
/// </summary>
public sealed class MatchCallbackClient
{
    private readonly ILogger<MatchCallbackClient> _logger;

    public MatchCallbackClient(ILogger<MatchCallbackClient> logger)
    {
        _logger = logger;
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
