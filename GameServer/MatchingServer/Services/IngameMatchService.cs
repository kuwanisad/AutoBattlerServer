using Grpc.Core;
using MatchingServer;
using MatchingServer.Services;

namespace MatchingServer.Services;

/// <summary>
/// 게임 에이전트가 호출하는 gRPC 서비스.
/// RegisterServer, UnregisterServer, PrepareMatchResult를 처리한다.
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

    public override Task<RegisterServerResponse> RegisterServer(
        RegisterServerRequest request, ServerCallContext context)
    {
        _matchEngine.RegisterServer(request.ServerId, request.ServerType, request.Address);
        return Task.FromResult(new RegisterServerResponse { Success = true });
    }

    public override Task<UnregisterServerResponse> UnregisterServer(
        UnregisterServerRequest request, ServerCallContext context)
    {
        _matchEngine.UnregisterServer(request.ServerId);
        return Task.FromResult(new UnregisterServerResponse { Success = true });
    }

    public override Task<PrepareMatchResponse> PrepareMatch(
        PrepareMatchRequest request, ServerCallContext context)
    {
        // 매칭 서버가 게임 에이전트에 호출하는 RPC이므로 여기서는 구현하지 않는다.
        // 실제 구현은 게임 에이전트의 IngameMatchService에 있다.
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