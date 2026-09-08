using Microsoft.Extensions.Logging;

namespace AccountServer;

/// <summary>
/// 우편함이 AccountServerConfig.MailboxCapacity를 넘으면 밀어낸다. 밀어내는 순서는
/// 수령됨(Claimed) > 만료됨(Expired) > 오래된 것(CreatedAtUnixMs) 순 - 이미 받았거나
/// 이미 못 받게 된 우편부터 먼저 치우고, 그래도 넘치면 그때 가장 오래된 것부터.
///
/// 아직 미수령/미만료 상태로 밀려나야 하는 우편은 그 타입(MailType)에 따라 다르게
/// 처리한다 - ItemProvision(보상, 유저가 받아서 손해가 없음)은 밀려나기 전에 자동으로
/// 수령 처리하고, 그 외(GuildInvite처럼 수락이 실제 결정이라 이득이 불분명한 것)는
/// 그냥 삭제(거부)한다.
///
/// [메모 - 아직 정답을 모름] ItemProvision을 자동 수령 처리하는 게 지금은 "Claimed=true로
/// 표시"가 전부다 - 실제 인벤토리 시스템이 없어서 "인벤토리에 못 넣으면 그냥 증발시킬지,
/// 아니면 어떻게든 최대한 채워 넣을지"를 아직 결정 못 했다. 실제 인벤토리 시스템이
/// 생기면 이 지점(TryClaimAsync 호출 자리)에서 그 판단이 이뤄져야 한다(TODO.md 참고).
/// </summary>
public sealed class MailboxEvictor
{
    private readonly IMailRepository _repository;
    private readonly ILogger<MailboxEvictor> _logger;

    public MailboxEvictor(IMailRepository repository, ILogger<MailboxEvictor> logger)
    {
        _repository = repository;
        _logger = logger;
    }

    public async Task EvictIfOverCapacityAsync(string playerId)
    {
        var mails = await _repository.GetForPlayerAsync(playerId);
        var overflow = mails.Count - AccountServerConfig.MailboxCapacity;
        if (overflow <= 0) return;

        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        // 정렬 하나로 세 단계 우선순위를 한 번에 표현 - 수령됨(true가 먼저) →
        // 만료됨(true가 먼저) → 오래된 것(작은 값이 먼저).
        var targets = mails
            .OrderByDescending(m => m.Claimed)
            .ThenByDescending(m => m.ExpiresAtUnixMs != 0 && m.ExpiresAtUnixMs < now)
            .ThenBy(m => m.CreatedAtUnixMs)
            .Take(overflow)
            .ToList();

        foreach (var mail in targets)
        {
            if (!mail.Claimed && IsAutoClaimEligible(mail.MailType))
            {
                await _repository.TryClaimAsync(playerId, mail.MailId);
            }

            await _repository.DeleteAsync(playerId, mail.MailId);
        }

        _logger.LogInformation("우편함 밀어내기: {PlayerId} {Count}개 정리(상한선={Capacity})",
            playerId, targets.Count, AccountServerConfig.MailboxCapacity);
    }

    private static bool IsAutoClaimEligible(GameServer.Common.MailType type) => type switch
    {
        GameServer.Common.MailType.ItemProvision => true,
        _ => false, // 알 수 없는 타입/GuildInvite 등은 안전하게 거부
    };
}
