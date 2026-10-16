using Grpc.Core;
using MatchingServer;
using MatchingServer.Services;

namespace MatchingServer.Services;

/// <summary>
/// 로비 서버가 호출하는 gRPC 서비스.
/// JoinMatch, LeaveMatch를 처리한다 (요청-응답이 필요한 것만 단항으로 남아있다).
/// 매칭 결과 통보(NotifyMatch)는 이제 MatchConnectionControl.Connect 스트림으로 처리하므로
/// 여기 없다 - MatchConnectionService/MatchEngine 참고.
/// </summary>
public sealed class LobbyMatchService : LobbyMatchControl.LobbyMatchControlBase
{
    private readonly MatchEngine _matchEngine;
    private readonly ILogger<LobbyMatchService> _logger;

    public LobbyMatchService(MatchEngine matchEngine, ILogger<LobbyMatchService> logger)
    {
        _matchEngine = matchEngine;
        _logger = logger;
    }

    public override Task<JoinMatchResponse> JoinMatch(JoinMatchRequest request, ServerCallContext context)
    {
        var failReason = _matchEngine.JoinMatch(
            request.PlayerId,
            request.LobbyServerId,
            request.SkillScore,
            request.GameType);

        return Task.FromResult(failReason == null
            ? new JoinMatchResponse { Success = true }
            : new JoinMatchResponse
            {
                Success = false,
                Reason = failReason.Value,
                Detail = $"game_type={request.GameType}",
            });
    }

    public override Task<LeaveMatchResponse> LeaveMatch(LeaveMatchRequest request, ServerCallContext context)
    {
        var success = _matchEngine.LeaveMatch(request.PlayerId);
        return Task.FromResult(new LeaveMatchResponse { Success = success });
    }
}
