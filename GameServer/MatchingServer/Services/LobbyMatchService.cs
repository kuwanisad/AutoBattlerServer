using Grpc.Core;
using MatchingServer;
using MatchingServer.Services;

namespace MatchingServer.Services;

/// <summary>
/// 로비 서버가 호출하는 gRPC 서비스.
/// JoinMatch, LeaveMatch를 처리한다.
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
        var success = _matchEngine.JoinMatch(
            request.PlayerId,
            request.LobbyServerId,
            request.SkillScore);

        return Task.FromResult(new JoinMatchResponse { Success = success });
    }

    public override Task<LeaveMatchResponse> LeaveMatch(LeaveMatchRequest request, ServerCallContext context)
    {
        var success = _matchEngine.LeaveMatch(request.PlayerId);
        return Task.FromResult(new LeaveMatchResponse { Success = success });
    }

    public override Task<MatchNotificationResponse> NotifyMatch(MatchNotification request, ServerCallContext context)
    {
        // 매칭 서버가 로비 서버에 통보하는 RPC이므로 여기서는 구현하지 않는다.
        // 실제 구현은 로비 서버의 LobbyMatchService에 있다.
        throw new RpcException(new Status(StatusCode.Unimplemented, "매칭 서버는 이 RPC를 호출하지 않습니다."));
    }
}