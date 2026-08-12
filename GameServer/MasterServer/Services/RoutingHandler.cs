using Grpc.Core;
using MasterServer.Registry;
using MasterServer.Routing;
using Shared.Common;
using GameServer.Common;

namespace MasterServer.Services;

/// <summary>
/// RequestIngameAssignment RPC의 실제 로직 담당.
/// 로비가 매치를 확정했을 때 어느 인게임 인스턴스로 보낼지 결정한다.
/// </summary>
internal sealed class RoutingHandler
{
    private readonly InstanceRegistry _registry;
    private readonly IRoutingStrategy _routingStrategy;
    private readonly ILogger _logger;

    internal RoutingHandler(
        InstanceRegistry registry,
        IRoutingStrategy routingStrategy,
        ILogger logger)
    {
        _registry = registry;
        _routingStrategy = routingStrategy;
        _logger = logger;
    }

    internal Task<IngameAssignmentResponse> HandleAsync(IngameAssignmentRequest request)
    {
        var candidates = _registry.GetFreshByType(ServerType.Ingame, ConnectionConstants.HeartbeatTimeout);
        var selected = _routingStrategy.SelectIngameInstance(candidates, request.PartySize);

        if (selected is null)
        {
            throw new RpcException(new Status(
                StatusCode.ResourceExhausted,
                "현재 여유 있는 인게임 인스턴스가 없습니다."));
        }

        _logger.LogInformation(
            "라우팅 결정: 로비 {LobbyId} -> 인게임 {IngameId} ({Address})",
            request.RequestingLobbyInstanceId, selected.InstanceId, selected.Address);

        return Task.FromResult(new IngameAssignmentResponse
        {
            IngameInstanceId = selected.InstanceId,
            Address = selected.Address,
        });
    }
}