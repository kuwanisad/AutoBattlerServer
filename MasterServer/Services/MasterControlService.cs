using Grpc.Core;
using MasterServer.Registry;
using MasterServer.Routing;

namespace MasterServer.Services;

/// <summary>
/// gRPC 서비스 진입점. MasterControlBase가 정의한 RPC 메서드를 override해서
/// 각 전담 핸들러로 위임하는 역할만 한다.
///
/// gRPC 디스패치가 "클래스 하나"를 서비스로 등록하는 방식이라, 물리적으로
/// 여러 RPC를 다른 클래스에 나눠 넣는 것이 불가능하다. 그래서 이 클래스를
/// 얇은 진입점으로 유지하고, 실제 로직은 목적별 핸들러 클래스에 위임한다.
/// </summary>
public sealed class MasterControlService : MasterControl.MasterControlBase
{
    private readonly ConnectionHandler _connectionHandler;
    private readonly RoutingHandler _routingHandler;

    public MasterControlService(
        InstanceRegistry registry,
        IRoutingStrategy routingStrategy,
        ILogger<MasterControlService> logger)
    {
        _connectionHandler = new ConnectionHandler(registry, logger);
        _routingHandler = new RoutingHandler(registry, routingStrategy, logger);
    }

    public override Task Connect(
        IAsyncStreamReader<MainServerMessage> requestStream,
        IServerStreamWriter<MasterMessage> responseStream,
        ServerCallContext context) =>
        _connectionHandler.HandleAsync(requestStream, responseStream, context.CancellationToken);

    public override Task<IngameAssignmentResponse> RequestIngameAssignment(
        IngameAssignmentRequest request,
        ServerCallContext context) =>
        _routingHandler.HandleAsync(request);
}
