using MainServer.Common.MasterConnection;
using Shared.Common;
using Shared.Common.Account;
using Shared.Common.Redis;

namespace GameAgent.Services;

/// <summary>
/// 게임 에이전트 전용 MainServerIntermediary.
/// - 받는 쪽: 로비가 RegisterIncomingTransfers로 보낸 티켓들을 등록(로비→인게임 이동).
/// - 보내는 쪽: 방 정상 종료 시 로비한테 티켓을 통지(인게임→로비 이동).
/// </summary>
public sealed class IngameAgentMainServerIntermediary : MainServerIntermediary
{
    public IngameAgentMainServerIntermediary(
        ISessionRegistry sessionRegistry,
        MasterConnectionService masterConnection,
        ILogger<IngameAgentMainServerIntermediary> logger)
    {
        // 로비→인게임 이동 중 티켓 TTL 안에 안 온 클라의 세션을 지운다(MainServerIntermediary 참고).
        RemoveSessionOnTicketExpiry(sessionRegistry, () => masterConnection.InstanceId, logger);
    }

    /// <summary>IngameControlService.RegisterIncomingTransfers RPC 핸들러가 호출.</summary>
    public void OnRegisterIncomingTransfersReceived(IEnumerable<(string PlayerId, string Ticket)> tickets) =>
        RegisterBatch(tickets);

    protected override async Task NotifyTargetAsync(Dictionary<string, string> playerIdToTicket, string targetAddress)
    {
        using var channel = GrpcChannelFactory.Create(targetAddress);
        var client = new LobbyServer.LobbyControl.LobbyControlClient(channel);

        var request = new LobbyServer.RegisterIncomingTransfersRequest();
        foreach (var (playerId, ticket) in playerIdToTicket)
        {
            request.Tickets.Add(new LobbyServer.PlayerTicket { PlayerId = playerId, Ticket = ticket });
        }

        await client.RegisterIncomingTransfersAsync(request);
    }
}
