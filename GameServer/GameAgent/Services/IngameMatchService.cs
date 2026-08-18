using Grpc.Core;
using Grpc.Net.Client;
using MainServer.Common.MasterConnection;
using MatchingServer;
using Microsoft.Extensions.Logging;

namespace GameAgent.Services;

/// <summary>
/// 매칭 서버가 게임 에이전트에 호출하는 gRPC 서비스.
/// PrepareMatch를 처리하고 결과를 매칭 서버에 통보한다.
/// </summary>
public sealed class IngameMatchService : IngameMatchControl.IngameMatchControlBase
{
    private readonly GameServerHandler _handler;
    private readonly MasterConnectionService _masterConnection;
    private readonly ILogger<IngameMatchService> _logger;

    public IngameMatchService(
        GameServerHandler handler,
        MasterConnectionService masterConnection,
        ILogger<IngameMatchService> logger)
    {
        _handler = handler;
        _masterConnection = masterConnection;
        _logger = logger;
    }

    public override async Task<PrepareMatchResponse> PrepareMatch(
        PrepareMatchRequest request, ServerCallContext context)
    {
        _logger.LogInformation("PrepareMatch 요청: {MatchId} ({Count}명)",
            request.MatchId, request.PlayerIds.Count);

        // 수용 가능 여부 확인
        var serverInfo = _handler.GetServerInfo();
        if (serverInfo == null || !serverInfo.IsReady)
        {
            _logger.LogWarning("PrepareMatch 거부 - 게임 서버 준비 안 됨: {MatchId}", request.MatchId);
            return new PrepareMatchResponse
            {
                Accepted = false,
                ErrorMessage = "게임 서버 준비 안 됨",
            };
        }

        var availableSlots = serverInfo.Capacity - serverInfo.CurrentLoad;
        if (availableSlots < request.PlayerIds.Count)
        {
            _logger.LogWarning("PrepareMatch 거부 - 수용 불가: {MatchId} (여유={Slots}, 요청={Count})",
                request.MatchId, availableSlots, request.PlayerIds.Count);
            return new PrepareMatchResponse
            {
                Accepted = false,
                ErrorMessage = "수용 불가",
            };
        }

        // 수락 후 비동기로 준비 완료 통보
        _ = Task.Run(async () =>
        {
            try
            {
                // TODO: 실제 방 생성 로직 추가
                await Task.Delay(100); // 방 준비 시뮬레이션

                if (!string.IsNullOrEmpty(_masterConnection.MatchingServerAddress))
                {
                    using var channel = GrpcChannel.ForAddress(_masterConnection.MatchingServerAddress);
                    var client = new IngameMatchControl.IngameMatchControlClient(channel);
                    await client.PrepareMatchResultAsync(new PrepareMatchResultRequest
                    {
                        MatchId = request.MatchId,
                        Success = true,
                    });
                    _logger.LogInformation("PrepareMatch 완료 통보: {MatchId}", request.MatchId);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "PrepareMatch 준비 실패: {MatchId}", request.MatchId);

                if (!string.IsNullOrEmpty(_masterConnection.MatchingServerAddress))
                {
                    using var channel = GrpcChannel.ForAddress(_masterConnection.MatchingServerAddress);
                    var client = new IngameMatchControl.IngameMatchControlClient(channel);
                    await client.PrepareMatchResultAsync(new PrepareMatchResultRequest
                    {
                        MatchId = request.MatchId,
                        Success = false,
                        ErrorMessage = ex.Message,
                    });
                }
            }
        });

        return new PrepareMatchResponse { Accepted = true };
    }

    public override Task<RegisterServerResponse> RegisterServer(
        RegisterServerRequest request, ServerCallContext context)
    {
        throw new RpcException(new Status(StatusCode.Unimplemented,
            "게임 에이전트는 이 RPC를 호출하지 않습니다."));
    }

    public override Task<UnregisterServerResponse> UnregisterServer(
        UnregisterServerRequest request, ServerCallContext context)
    {
        throw new RpcException(new Status(StatusCode.Unimplemented,
            "게임 에이전트는 이 RPC를 호출하지 않습니다."));
    }

    public override Task<PrepareMatchResultResponse> PrepareMatchResult(
        PrepareMatchResultRequest request, ServerCallContext context)
    {
        throw new RpcException(new Status(StatusCode.Unimplemented,
            "게임 에이전트는 이 RPC를 호출하지 않습니다."));
    }
}