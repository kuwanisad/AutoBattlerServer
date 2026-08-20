using Grpc.Core;
using MatchingServer;
using MatchingServer.Services;

namespace MatchingServer.Services;

/// <summary>
/// 게임 에이전트가 호출하는 gRPC 서비스.
/// PrepareMatchResult를 처리한다 (요청-응답이 필요한 것만 단항으로 남아있다).
/// RegisterServer/UnregisterServer는 이제 MatchConnectionControl.Connect 스트림 접속/해제로
/// 대체됐다 - MatchConnectionService 참고.
/// PrepareMatch는 매칭 서버가 게임 에이전트에 호출하는 방향이라 여기서 구현하지 않는다
/// (실제 구현은 게임 에이전트의 IngameMatchService에 있다).
/// </summary>
public sealed class IngameMatchService : IngameMatchControl.IngameMatchControlBase
{
    private readonly MatchEngine _matchEngine;
    private readonly ILogger<IngameMatchService> _logger;

    public IngameMatchService(MatchEngine matchEngine, ILogger<IngameMatchService> logger)
    {
        _matchEngine = matchEngine;
        _logger = logger;
    }

    public override Task<PrepareMatchResponse> PrepareMatch(
        PrepareMatchRequest request, ServerCallContext context)
    {
        throw new RpcException(new Status(StatusCode.Unimplemented, "매칭 서버는 이 RPC를 호출하지 않습니다."));
    }

    public override async Task<PrepareMatchResultResponse> PrepareMatchResult(
        PrepareMatchResultRequest request, ServerCallContext context)
    {
        await _matchEngine.HandlePrepareMatchResultAsync(
            request.MatchId, request.Success, request.ErrorMessage);

        return new PrepareMatchResultResponse { Acknowledged = true };
    }
}
