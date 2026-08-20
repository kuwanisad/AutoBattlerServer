using Grpc.Core;
using MatchingServer.Registry;

namespace MatchingServer.Services;

public sealed class MatchConnectionService : MatchConnectionControl.MatchConnectionControlBase
{
    private readonly MatchConnectionHandler _handler;

    public MatchConnectionService(
        ConnectedServerRegistry registry,
        MatchEngine matchEngine,
        ILogger<MatchConnectionService> logger)
    {
        _handler = new MatchConnectionHandler(registry, matchEngine, logger);
    }

    public override Task Connect(
        IAsyncStreamReader<ServerToMatchMessage> requestStream,
        IServerStreamWriter<MatchToServerMessage> responseStream,
        ServerCallContext context) =>
        _handler.HandleAsync(requestStream, responseStream, context);
}