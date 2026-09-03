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

    /// <summary>
    /// 하위 서버가 "이 플레이어 우편함이 갱신됐다"는 신호를 보내면 호출된다. 실제
    /// 처리(이 게임 에이전트에 접속 중인지 확인 후 릴레이)는 GameServerHandler가
    /// 담당한다 - 얇은 껍데기 원칙을 여기서도 유지.
    /// </summary>
    public override async Task<NotifyMailUpdatedResponse> NotifyMailUpdated(
        NotifyMailUpdatedRequest request, ServerCallContext context)
    {
        var success = await _handler.NotifyMailUpdatedAsync(request.PlayerId);
        return new NotifyMailUpdatedResponse { Success = success };
    }
}
