using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Shared.Common.Redis;

namespace Shared.Common.Account;

/// <summary>
/// 클라의 서버 이동(로비↔인게임) 시 발급하는 일회용 임시 티켓을 담당한다. 훔친 영구
/// 토큰으로 "진짜 클라보다 먼저 목적지 서버에 도착"하는 걸 막기 위한 것 - 진짜 세션
/// 토큰(CurrentToken/PreviousToken)은 건드리지 않고, 완전히 별도의 짧은 수명 값 하나만
/// 추가로 검증한다.
///
/// 받는 쪽(목적지 서버)과 보내는 쪽(소스 서버) 역할을 이 클래스 하나가 다 갖는다 -
/// 로비/게임 에이전트가 서로에게 번갈아 양쪽 역할을 하기 때문. 클라한테 통지하는 방식
/// (RoomClosed/MatchFound 등 기존 메시지 재사용)과 상대 서버한테 통지하는 방식(어떤
/// 통로를 쓸지)만 서버마다 다르므로, 후자를 추상 메서드로 위임한다.
/// </summary>
public abstract class MainServerIntermediary
{
    private static readonly TimeSpan TicketTtl = TimeSpan.FromSeconds(10);

    private readonly ConcurrentDictionary<string, PendingTicket> _pending = new();

    /// <summary>티켓 만료(=그 시간 안에 클라가 안 옴) 시 서버마다 다른 정리 로직을 위임받는다 -
    /// 이 클래스는 "언제"만 판단하고 "무엇을"은 모른다.</summary>
    public event Action<string>? OnTicketExpired;

    /// <summary>
    /// 티켓 만료 시 그 플레이어의 세션을 세션 레지스트리에서 지우도록 연결한다(받는 쪽 서버가
    /// 생성자에서 호출). 시간 안에 안 온 클라의 세션이 "이동 중 + 소유자 = 이 서버"로 영원히 남으면,
    /// 그 클라가 로그인 서버로 재진입할 때마다 이 서버로 다시 안내돼 티켓 없이 거부되는 루프에 빠진다 -
    /// 지우면 로그인 서버가 "살아있는 세션 없음"으로 거부해서 처음부터 다시 로그인하게 된다.
    /// 지우기 직전에 "아직 이동 중이고 소유자가 나"인지 다시 확인한다 - 그 사이 다른 경로로
    /// 상태가 바뀐 세션은 건드리지 않는다.
    /// </summary>
    protected void RemoveSessionOnTicketExpiry(ISessionRegistry sessionRegistry, Func<string> selfServerId, ILogger logger)
    {
        OnTicketExpired += playerId => _ = RemoveIfStillTransferringAsync(playerId);

        async Task RemoveIfStillTransferringAsync(string playerId)
        {
            try
            {
                var session = await sessionRegistry.GetAsync(playerId);
                if (session == null) return;

                if (session.Status != PlayerStatus.Moving || session.OwnerServerId != selfServerId())
                {
                    logger.LogInformation("전입 티켓 만료 - 세션 상태가 이미 바뀌어 그대로 둠: {PlayerId} Status={Status} Owner={Owner}",
                        playerId, session.Status, session.OwnerServerId);
                    return;
                }

                await sessionRegistry.RemoveAsync(playerId);
                logger.LogWarning("전입 티켓 만료 - 시간 안에 도착하지 않아 세션 삭제: {PlayerId} (TTL={Ttl}초)",
                    playerId, TicketTtl.TotalSeconds);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "전입 티켓 만료 처리 실패: {PlayerId}", playerId);
            }
        }
    }

    // === 받는 쪽 ===

    /// <summary>목적지 서버가 여러 명의 티켓을 한 번에 등록한다 - 상대 서버로부터 통지를
    /// 받는 RPC 핸들러가 호출.</summary>
    protected void RegisterBatch(IEnumerable<(string PlayerId, string Ticket)> entries)
    {
        foreach (var (playerId, ticket) in entries)
        {
            var pending = new PendingTicket(ticket, DateTime.UtcNow + TicketTtl);
            _pending[playerId] = pending;
            _ = ExpireAfterTtlAsync(playerId, pending);
        }
    }

    private async Task ExpireAfterTtlAsync(string playerId, PendingTicket pending)
    {
        await Task.Delay(TicketTtl);

        // 그 사이 이미 소비되거나(TryConsume) 다른 티켓으로 덮어써졌으면 아무 일도 안 함 -
        // 지금 딕셔너리에 있는 게 여전히 이 pending 인스턴스일 때만 진짜 만료로 취급.
        if (_pending.TryGetValue(playerId, out var current) && current == pending)
        {
            _pending.TryRemove(playerId, out _);
            OnTicketExpired?.Invoke(playerId);
        }
    }

    /// <summary>클라가 실제로 도착해서 EnterLobby/ConnectionApproval을 부를 때 호출한다.
    /// 맞는 티켓이면 원자적으로 1회만 통과하고 즉시 제거한다(재사용 방지). 틀린 티켓이면 거부만
    /// 하고 진짜 티켓은 TTL까지 남겨둔다.</summary>
    public bool TryConsume(string playerId, string submittedTicket)
    {
        if (!_pending.TryGetValue(playerId, out var pending))
        {
            return false; // 애초에 등록된 적 없거나, 이미 소비됐거나, 이미 만료돼서 지워짐
        }

        if (pending.Ticket != submittedTicket || DateTime.UtcNow >= pending.ExpiresAt)
        {
            // 틀린 티켓으로는 진짜 티켓을 지우지 않는다 - 예전엔 맞든 틀리든 일단 꺼내서 지웠는데,
            // 그러면 티켓 없이 들어온 재시도(로그인 서버 재진입 경로는 빈 티켓으로 접속함) 한 번에
            // 진짜 티켓이 사라지고, 그 바람에 만료 이벤트(ExpireAfterTtlAsync)도 안 와서 세션이
            // "이동 중 + 소유자 = 이 서버"로 영원히 남았다(재진입할 때마다 거부되는 무한 루프).
            // 티켓은 무작위 GUID라 TTL(10초) 안에 맞혀볼 수 있는 값이 아니어서, 틀린 시도를 남겨둬도
            // 위험은 사실상 없다.
            return false;
        }

        // 맞는 티켓은 정확히 그 인스턴스일 때만 지운다 - 동시에 들어온 두 요청 중 하나만 통과(재사용 방지).
        return _pending.TryRemove(new KeyValuePair<string, PendingTicket>(playerId, pending));
    }

    /// <summary>이 플레이어 앞으로 등록된 티켓이 지금 있는지만 확인한다(소비 안 함, 부작용 없음).
    /// "티켓 검증이 필요한 상황인지"를 이걸로 판단하면 안 된다 - 티켓은 TTL이 지나면 지워지므로,
    /// 만료된 이동이 "티켓이 필요 없는 재접속"과 구분되지 않는다. 검증 필요 여부는 세션 상태
    /// (Status == Moving + OwnerServerId == 이 서버)로 판단하고, 이 메서드는 로그/진단용으로만 쓴다.</summary>
    public bool HasPendingTicket(string playerId) => _pending.ContainsKey(playerId);

    // === 보내는 쪽 ===

    /// <summary>여러 명분의 티켓을 한 번에 발급하고, 상대 서버한테 통지까지 처리한다.
    /// 반환값(playerId → ticket)은 호출부가 각 플레이어의 기존 이동 통지 메시지
    /// (RoomClosed/MatchFound)에 각자 실어서 클라한테 전달하는 데 쓴다.</summary>
    public async Task<Dictionary<string, string>> IssueAndNotifyAsync(
        IEnumerable<string> playerIds, string targetAddress)
    {
        var tickets = playerIds.ToDictionary(id => id, _ => GenerateRandomTicket());
        await NotifyTargetAsync(tickets, targetAddress);
        return tickets;
    }

    /// <summary>상대 서버한테 발급된 티켓들을 실제로 전달하는 방법. 서버마다 다른
    /// 통로(어떤 gRPC 서비스/클라이언트를 쓰는지)를 쓰므로 구현부에 위임한다.</summary>
    protected abstract Task NotifyTargetAsync(Dictionary<string, string> playerIdToTicket, string targetAddress);

    private static string GenerateRandomTicket() => Guid.NewGuid().ToString("N");

    private sealed record PendingTicket(string Ticket, DateTime ExpiresAt);
}