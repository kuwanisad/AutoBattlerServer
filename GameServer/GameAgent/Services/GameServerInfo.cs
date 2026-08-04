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

    /// <summary>현재 활성 매치 수.</summary>
    public int CurrentLoad { get; set; }

    /// <summary>수용 가능한 최대 매치 수.</summary>
    public int Capacity { get; set; }

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
