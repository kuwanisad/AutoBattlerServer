using Grpc.Core;
using Grpc.Net.Client;
using MainServer.Common.MasterConnection;
using MatchingServer;
using Microsoft.Extensions.Logging;

namespace GameAgent.Services;

/// <summary>
/// 매칭 서버가 게임 에이전트에 호출하는 gRPC 서비스.
/// PrepareMatch를 처리하고 결과를 매칭 서버에 통보한다.
///
/// PrepareMatch 자체는 즉시 응답해야 하는 단항 RPC라 "수용 가능한지"만 빠르게 확인해서
/// 답하고, 실제 방 생성(게임 서버와의 비동기 왕복)은 백그라운드로 진행한 뒤
/// PrepareMatchResult로 별도 보고한다. GameServerHandler.RequestOpenRoomAsync가 그
/// 비동기 왕복(요청 전달 후 RoomOpened가 올 때까지 대기)을 담당한다.
/// </summary>
public sealed class IngameMatchService : IngameMatchControl.IngameMatchControlBase
{
    private static readonly TimeSpan OpenRoomTimeout = TimeSpan.FromSeconds(10);

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

    public override Task<PrepareMatchResponse> PrepareMatch(
        PrepareMatchRequest request, ServerCallContext context)
    {
        _logger.LogInformation("PrepareMatch 요청: {MatchId} ({Count}명)",
            request.MatchId, request.PlayerIds.Count);

        // 수용 가능 여부만 즉시 확인 - 실제 방 생성은 비동기라 여기서 안 기다린다.
        var serverInfo = _handler.GetServerInfo();
        if (serverInfo == null || !serverInfo.IsReady)
        {
            _logger.LogWarning("PrepareMatch 거부 - 게임 서버 준비 안 됨: {MatchId}", request.MatchId);
            return Task.FromResult(new PrepareMatchResponse
            {
                Accepted = false,
                ErrorMessage = "게임 서버 준비 안 됨",
            });
        }

        var availableSlots = serverInfo.Capacity - serverInfo.CurrentLoad;
        if (availableSlots < request.PlayerIds.Count)
        {
            _logger.LogWarning("PrepareMatch 거부 - 수용 불가: {MatchId} (여유={Slots}, 요청={Count})",
                request.MatchId, availableSlots, request.PlayerIds.Count);
            return Task.FromResult(new PrepareMatchResponse
            {
                Accepted = false,
                ErrorMessage = "수용 불가",
            });
        }

        // 수락 후, 실제 방 생성(게임 서버와의 비동기 왕복)은 백그라운드로 진행하고
        // 결과는 끝나는 대로 PrepareMatchResult로 따로 보고한다.
        _ = ProcessOpenRoomAsync(request.MatchId, request.PlayerIds.ToList(), request.GameType);

        return Task.FromResult(new PrepareMatchResponse { Accepted = true });
    }

    private async Task ProcessOpenRoomAsync(string matchId, List<string> playerIds, GameServer.Common.GameType gameType)
    {
        bool success;
        string errorMessage = string.Empty;

        try
        {
            var roomOpened = await _handler.RequestOpenRoomAsync(
                matchId, playerIds, gameType, OpenRoomTimeout, CancellationToken.None);

            success = roomOpened.Success;
            errorMessage = roomOpened.ErrorMessage;
        }
        catch (OperationCanceledException)
        {
            success = false;
            errorMessage = "방 생성 응답 시간 초과";
            _logger.LogWarning("방 생성 타임아웃: {MatchId}", matchId);
        }
        catch (Exception ex)
        {
            success = false;
            errorMessage = ex.Message;
            _logger.LogWarning(ex, "방 생성 대기 실패: {MatchId}", matchId);
        }

        await ReportPrepareMatchResultAsync(matchId, success, errorMessage);
    }

    private async Task ReportPrepareMatchResultAsync(string matchId, bool success, string errorMessage)
    {
        if (string.IsNullOrEmpty(_masterConnection.MatchingServerAddress))
        {
            _logger.LogWarning("매칭 서버 주소 없음 - PrepareMatchResult 보고 실패: {MatchId}", matchId);
            return;
        }

        try
        {
            using var channel = GrpcChannel.ForAddress(_masterConnection.MatchingServerAddress);
            var client = new IngameMatchControl.IngameMatchControlClient(channel);
            await client.PrepareMatchResultAsync(new PrepareMatchResultRequest
            {
                MatchId = matchId,
                Success = success,
                ErrorMessage = errorMessage ?? string.Empty,
                IngameAddress = _handler.ClientAddress,
            });

            _logger.LogInformation("PrepareMatch 결과 통보: {MatchId} success={Success}", matchId, success);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "PrepareMatchResult 통보 실패: {MatchId}", matchId);
        }
    }

    public override Task<PrepareMatchResultResponse> PrepareMatchResult(
        PrepareMatchResultRequest request, ServerCallContext context)
    {
        throw new RpcException(new Status(StatusCode.Unimplemented,
            "게임 에이전트는 이 RPC를 호출하지 않습니다."));
    }

    public override async Task<CancelRoomResponse> CancelRoom(
        CancelRoomRequest request, ServerCallContext context)
    {
        _logger.LogInformation("CancelRoom 요청 수신: {MatchId} ({Reason})", request.MatchId, request.Reason);

        await _handler.CloseRoomAndRedirectAsync(request.MatchId, request.Reason);

        return new CancelRoomResponse { Acknowledged = true };
    }

    public override Task<RequestLobbyAddressResponse> RequestLobbyAddress(
        RequestLobbyAddressRequest request, ServerCallContext context)
    {
        throw new RpcException(new Status(StatusCode.Unimplemented,
            "게임 에이전트는 이 RPC를 호출하지 않습니다 (매칭 서버에 이 RPC를 호출하는 쪽입니다)."));
    }
}