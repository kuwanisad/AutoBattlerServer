using IngameServer;
using Microsoft.Extensions.Logging;

namespace GameAgent.Services;

/// <summary>
/// "직접 대상" - 지금 이 게임 에이전트가 담당하는 게임 서버(Unity)에 접속 중인 클라한테
/// 우편함을 전달한다(GameServerInfo.OutboundQueue로 릴레이). 제3자(다른 곳에 있거나
/// 오프라인)한테는 RemoteMailNotifier를 쓴다.
/// </summary>
public sealed class IngameMailDeliverer
{
    private readonly IngameAccountServerIntermediary _accountServer;
    private readonly ILogger<IngameMailDeliverer> _logger;

    public IngameMailDeliverer(IngameAccountServerIntermediary accountServer, ILogger<IngameMailDeliverer> logger)
    {
        _accountServer = accountServer;
        _logger = logger;
    }

    /// <summary>이미 어딘가에서 지급된 우편함을 다시 조회해서 전달만 한다(접속 시
    /// 자동 제공, 신호 수신 시 즉시 제공, 클라의 능동 요청 - 전부 이걸 재사용).</summary>
    public async Task DeliverAsync(string playerId, GameServerInfo serverInfo)
    {
        var response = await _accountServer.GetMailListAsync(playerId);

        var mailProvided = new MailProvided { PlayerId = playerId };
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

        serverInfo.OutboundQueue.Writer.TryWrite(new GameAgentMessage { MailProvided = mailProvided });

        // 자동 제공/신호 즉시 제공이 실제로 동작했는지 로그로 확인 가능하게 함 - 예전엔
        // 이 메서드가 조용히 성공/실패해서 디버깅 시 흔적이 안 남았음.
        _logger.LogInformation("우편함 전달: {PlayerId} 우편개수={Count}", playerId, mailProvided.Mails.Count);
    }
}
