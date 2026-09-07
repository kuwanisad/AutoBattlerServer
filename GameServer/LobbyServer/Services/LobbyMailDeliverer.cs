using LobbyServer;
using Microsoft.Extensions.Logging;
using Shared.Common.Account;

namespace LobbyServer.Services;

/// <summary>
/// "직접 대상" - 지금 이 로비에 붙어있는 클라한테 우편함을 전달한다(StayConnected
/// 채널로 push). 제3자(다른 곳에 있거나 오프라인)한테는 RemoteMailNotifier를 쓴다.
/// </summary>
public sealed class LobbyMailDeliverer
{
    private readonly LobbyAccountServerIntermediary _accountServer;
    private readonly LobbyConnectionRegistry _connectionRegistry;
    private readonly ILogger<LobbyMailDeliverer> _logger;

    public LobbyMailDeliverer(
        LobbyAccountServerIntermediary accountServer,
        LobbyConnectionRegistry connectionRegistry,
        ILogger<LobbyMailDeliverer> logger)
    {
        _accountServer = accountServer;
        _connectionRegistry = connectionRegistry;
        _logger = logger;
    }

    /// <summary>지급까지 하고 바로 전달한다(상점 구매 등 - 지금 나랑 붙어있는 클라 확정).</summary>
    public async Task GrantAndDeliverDirectAsync(
        string playerId, string sourceEventId, string mailType,
        string displayContent, string payloadJson, long expiresAtUnixMs = 0)
    {
        var grantResponse = await _accountServer.GrantMailAsync(
            playerId, sourceEventId, mailType, displayContent, payloadJson, expiresAtUnixMs);

        if (!grantResponse.Success)
        {
            _logger.LogWarning("우편 지급 거부: {PlayerId} MailType={MailType} {Error}",
                playerId, mailType, grantResponse.ErrorMessage);
            return;
        }

        await DeliverAsync(playerId);
    }

    /// <summary>이미 어딘가에서 지급된 우편함을 다시 조회해서 전달만 한다(접속 시
    /// 자동 제공, 신호 수신 시 즉시 제공, 클라의 능동 요청 - 전부 이걸 재사용).</summary>
    public async Task<MailProvided> DeliverAsync(string playerId)
    {
        var response = await _accountServer.GetMailListAsync(playerId);

        var mailProvided = new MailProvided();
        foreach (var m in response.Mails)
        {
            mailProvided.Mails.Add(new MailInfo
            {
                MailId = m.MailId,
                MailType = m.MailType,
                DisplayContent = m.DisplayContent,
                CreatedAtUnixMs = m.CreatedAtUnixMs,
                ExpiresAtUnixMs = m.ExpiresAtUnixMs,
                Claimed = m.Claimed,
            });
        }

        var delivered = _connectionRegistry.TryGet(playerId, out var channel);
        if (delivered)
        {
            channel.Writer.TryWrite(new StayConnectedEvent { MailProvided = mailProvided });
        }

        // 자동 제공/신호 즉시 제공이 실제로 동작했는지 로그로 확인 가능하게 함 - 예전엔
        // 이 메서드가 조용히 성공/실패해서 디버깅 시 흔적이 안 남았음.
        _logger.LogInformation(
            "우편함 전달: {PlayerId} 우편개수={Count} 채널전달={Delivered}",
            playerId, mailProvided.Mails.Count, delivered);

        return mailProvided;
    }
}
