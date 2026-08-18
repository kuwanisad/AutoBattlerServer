using Grpc.Core;
using MasterServer.Registry;
using MasterServer.Routing;
using MasterServer.Security;
using Microsoft.Extensions.Options;
using Shared.Common.Redis;

namespace MasterServer.Services;

public sealed class MasterControlService : MasterControl.MasterControlBase
{
    private readonly ConnectionHandler _connectionHandler;
    private readonly RoutingHandler _routingHandler;

    public MasterControlService(
        InstanceRegistry registry,
        IServerAuthenticator authenticator,
        IOptions<RedisOptions> redisOptions,
        IRoutingStrategy routingStrategy,
        ILogger<MasterControlService> logger)
    {
        _connectionHandler = new ConnectionHandler(registry, authenticator, redisOptions.Value, logger);
        _routingHandler = new RoutingHandler(registry, routingStrategy, logger);
    }

    public override Task Connect(
        IAsyncStreamReader<MainServerMessage> requestStream,
        IServerStreamWriter<MasterMessage> responseStream,
        ServerCallContext context) =>
        _connectionHandler.HandleAsync(requestStream, responseStream, context);

    public override Task<IngameAssignmentResponse> RequestIngameAssignment(
        IngameAssignmentRequest request,
        ServerCallContext context) =>
        _routingHandler.HandleAsync(request);
}
