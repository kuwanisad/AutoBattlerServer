using System.Collections.Concurrent;
using System.Threading.Channels;
using IngameServer;

namespace GameAgent.Services;

/// <summary>
/// 연결된 게임 서버의 상태와 송신 큐를 담는다.
/// MasterServer의 InstanceInfo와 동일한 패턴.
/// </summary>
public sealed class GameServerInfo
{
    /// <summary>게임 서버로 보낼 메시지 큐. SendLoopAsync가 단독 소비한다.</summary>
    public Channel<GameAgentMessage> OutboundQueue { get; } =
        Channel.CreateUnbounded<GameAgentMessage>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
        });

    /// <summary>게임 서버가 준비 완료를 알렸는지 여부.</summary>
    public bool IsReady { get; set; }

    /// <summary>
    /// 지금 차지된 자리 수 - 인게임 서버(Unity)가 세서 GameServerStatus로 보낸 값을 그대로 저장한다.
    /// 게임 에이전트는 직접 더하거나 빼지 않는다(예전엔 PlayerJoined/Left로 직접 셌는데, 정상 종료된
    /// 방의 인원을 빼는 경로가 없어서 부하가 영구히 쌓였다).
    /// </summary>
    public int CurrentLoad { get; set; }

    /// <summary>수용 가능한 최대 자리 수(CurrentLoad와 같은 단위).</summary>
    public int Capacity { get; set; }

    // PrepareMatch를 수락했지만 아직 인게임 서버의 보고에 반영되지 않은 자리 수.
    // 인게임 서버는 OpenRoom을 받아 응접실을 만들 때 그 인원을 임시로 잡고 RoomOpened보다
    // 먼저 상태를 보내므로(같은 스트림이라 순서 보장), RoomOpened를 받은 시점에 여기서 뺀다.
    // 이게 없으면 수락~응접실 생성 사이에 들어온 PrepareMatch가 정원을 넘겨 수락될 수 있다.
    private readonly object _reservationLock = new();
    private int _pendingReservedSeats;

    /// <summary>
    /// 자리가 남으면 count만큼 예약하고 true. 남은 자리 = Capacity - (보고된 CurrentLoad + 예약 중).
    /// 동시에 들어온 PrepareMatch 둘이 같은 빈자리를 보고 둘 다 수락하지 않게 락 안에서 판단한다.
    /// </summary>
    public bool TryReserveSeats(int count, out int availableSeats)
    {
        lock (_reservationLock)
        {
            availableSeats = Capacity - CurrentLoad - _pendingReservedSeats;
            if (availableSeats < count) return false;

            _pendingReservedSeats += count;
            return true;
        }
    }

    /// <summary>예약을 푼다 - 방 생성 결과(성공이든 실패든)를 받은 뒤에 부른다.</summary>
    public void ReleaseReservedSeats(int count)
    {
        lock (_reservationLock)
        {
            _pendingReservedSeats = Math.Max(0, _pendingReservedSeats - count);
        }
    }

    // clientId → playerId 매핑
    // ConnectionApproval 승인 후 PlayerJoined/PlayerLeft에서 playerId를 참조할 때 사용한다.
    private readonly ConcurrentDictionary<ulong, string> _clientToPlayer = new();

    /// <summary>ClientId → PlayerId 매핑을 추가한다. ConnectionApproval 승인 시 호출한다.</summary>
    public void AddClientMapping(ulong clientId, string playerId)
        => _clientToPlayer[clientId] = playerId;

    /// <summary>ClientId로 PlayerId를 조회한다. 없으면 null을 반환한다.</summary>
    public string? GetPlayerId(ulong clientId)
        => _clientToPlayer.TryGetValue(clientId, out var playerId) ? playerId : null;

    /// <summary>PlayerId로 ClientId를 조회한다. 없으면 null을 반환한다.</summary>
    public ulong? GetClientId(string playerId)
    {
        foreach (var kvp in _clientToPlayer)
        {
            if (kvp.Value == playerId) return kvp.Key;
        }
        return null;
    }

    /// <summary>PlayerId로 매핑을 삭제한다. PlayerLeft 시 호출한다.</summary>
    public void RemoveClientMapping(string playerId)
    {
        var clientId = GetClientId(playerId);
        if (clientId.HasValue)
            _clientToPlayer.TryRemove(clientId.Value, out _);
    }
}
