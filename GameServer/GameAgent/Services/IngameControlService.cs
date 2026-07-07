using Grpc.Core;
using IngameServer;
using Microsoft.Extensions.Logging;

namespace GameAgent.Services;

/// <summary>
/// 게임 서버가 접속하는 gRPC 서비스 진입점.
/// 얇은 껍데기로 실제 로직은 GameServerHandler가 담당한다.
/// MasterServer의 MasterControlService와 동일한 패턴.
/// </summary>
public sealed class IngameControlService : IngameControl.IngameControlBase
{
    private readonly GameServerHandler _handler;
    private readonly ILogger<IngameControlService> _logger;

    public IngameControlService(
        GameServerHandler handler,
        ILogger<IngameControlService> logger)
    {
        _handler = handler;
        _logger = logger;
    }

    public override async Task Connect(
        IAsyncStreamReader<GameServerMessage> requestStream,
        IServerStreamWriter<GameAgentMessage> responseStream,
        ServerCallContext context)
    {
        _logger.LogInformation("게임 서버 Connect 시작");

        try
        {
            await _handler.HandleAsync(requestStream, responseStream, context.CancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "게임 서버 Connect 처리 중 오류");
            throw;
        }

        _logger.LogInformation("게임 서버 Connect 종료");
    }
}
