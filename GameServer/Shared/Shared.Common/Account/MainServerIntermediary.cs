using System.Collections.Concurrent;

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
    /// 원자적으로 1회만 통과(성공/실패 상관없이 즉시 제거 - 재사용 방지).</summary>
    public bool TryConsume(string playerId, string submittedTicket)
    {
        if (_pending.TryRemove(playerId, out var pending))
        {
            return pending.Ticket == submittedTicket && DateTime.UtcNow < pending.ExpiresAt;
        }
        return false; // 애초에 등록된 적 없거나, 이미 소비됐거나, 이미 만료돼서 지워짐
    }

    /// <summary>이 플레이어 앞으로 등록된 티켓이 지금 있는지만 확인한다(소비 안 함,
    /// 부작용 없음) - "티켓 검증이 필요한 상황인지"를 판단하는 용도. 로비→인게임
    /// 정상 전환이면 IssueAndNotifyAsync가 미리 등록해뒀을 테니 있어야 하고, 같은
    /// 서버로의 재접속이면 애초에 등록된 적이 없으니 없는 게 정상이다 - 그 경우까지
    /// TryConsume을 강제하면 재접속을 막아버린다.</summary>
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