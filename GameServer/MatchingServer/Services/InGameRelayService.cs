using Grpc.Core;
using MatchingServer;
using MatchingServer.Registry;
using MatchingServer.Services;

namespace MatchingServer.Services;

/// <summary>
/// 게임 에이전트가 호출하는 gRPC 서비스.
/// PrepareMatchResult, RequestLobbyAddress를 처리한다 (요청-응답이 필요한 것만 단항으로 남아있다).
/// RegisterServer/UnregisterServer는 이제 MatchConnectionControl.Connect 스트림 접속/해제로
/// 대체됐다 - MatchConnectionService 참고.
/// PrepareMatch/CancelRoom은 매칭 서버가 게임 에이전트에 호출하는 방향이라 여기서 구현하지
/// 않는다 (실제 구현은 게임 에이전트의 IngameMatchService에 있다).
/// </summary>
public sealed class InGameRelayService : IngameMatchControl.IngameMatchControlBase
{
    private readonly MatchEngine _matchEngine;
    private readonly ConnectedServerRegistry _connectedServers;
    private readonly ILogger<InGameRelayService> _logger;

    public InGameRelayService(
        MatchEngine matchEngine,
        ConnectedServerRegistry connectedServers,
        ILogger<InGameRelayService> logger)
    {
        _matchEngine = matchEngine;
        _connectedServers = connectedServers;
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
            request.MatchId, request.Success, request.ErrorMessage, request.IngameAddress);

        return new PrepareMatchResultResponse { Acknowledged = true };
    }

    public override Task<CancelRoomResponse> CancelRoom(
        CancelRoomRequest request, ServerCallContext context)
    {
        throw new RpcException(new Status(StatusCode.Unimplemented, "매칭 서버는 이 RPC를 호출하지 않습니다."));
    }

    /// <summary>
    /// 게임 에이전트가 방을 취소/제거하면서, 그 방에 있던 클라들을 보낼 로비 주소를 물어볼 때
    /// 호출한다. 어느 로비든 상관없으므로 접속 중인 로비 중 아무거나 하나를 돌려준다.
    /// </summary>
    public override Task<RequestLobbyAddressResponse> RequestLobbyAddress(
        RequestLobbyAddressRequest request, ServerCallContext context)
    {
        var lobby = _connectedServers.GetByType(GameServer.Common.ServerType.Lobby).FirstOrDefault();

        if (lobby == null)
        {
            _logger.LogWarning("로비 주소 요청 - 접속 중인 로비 없음");
            return Task.FromResult(new RequestLobbyAddressResponse { Found = false });
        }

        return Task.FromResult(new RequestLobbyAddressResponse
        {
            Found = true,
            LobbyServerId = lobby.ServerId,
            Address = lobby.Address,
        });
    }
}