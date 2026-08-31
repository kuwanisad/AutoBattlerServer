using Grpc.Core;
using MainServer.Common.MasterConnection;
using Microsoft.Extensions.Logging;
using Shared.Common.Auth;
using Shared.Common.Redis;

namespace LoginServer.Services;

public sealed class LoginControlService : LoginControl.LoginControlBase
{
    private readonly LoginConnectionHandler _handler;

    public LoginControlService(
        MasterConnectionService masterConnection,
        ISessionRegistry sessionRegistry,
        ITokenProvider tokenProvider,
        ILogger<LoginControlService> logger)
    {
        _handler = new LoginConnectionHandler(masterConnection, sessionRegistry, tokenProvider, logger);
    }

    public override Task Connect(
        IAsyncStreamReader<ClientToLoginMessage> requestStream,
        IServerStreamWriter<LoginToClientMessage> responseStream,
        ServerCallContext context) =>
        _handler.HandleAsync(requestStream, responseStream, context);
}
