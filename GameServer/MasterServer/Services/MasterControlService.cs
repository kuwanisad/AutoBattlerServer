using Grpc.Core;
using MasterServer.Registry;
using MasterServer.Security;
using Microsoft.Extensions.Options;
using Shared.Common.Redis;

namespace MasterServer.Services;

public sealed class MasterControlService : MasterControl.MasterControlBase
{
    private readonly ConnectionHandler _connectionHandler;

    public MasterControlService(
        InstanceRegistry registry,
        ActiveAnnouncements activeAnnouncements,
        IServerAuthenticator authenticator,
        IOptions<RedisOptions> redisOptions,
        ILogger<MasterControlService> logger)
    {
        _connectionHandler = new ConnectionHandler(registry, activeAnnouncements, authenticator, redisOptions.Value, logger);
    }

    public override Task Connect(
        IAsyncStreamReader<MainServerMessage> requestStream,
        IServerStreamWriter<MasterMessage> responseStream,
        ServerCallContext context) =>
        _connectionHandler.HandleAsync(requestStream, responseStream, context);
}