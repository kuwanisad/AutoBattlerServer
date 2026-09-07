using System.Collections.Concurrent;
using System.Threading.Channels;

namespace LobbyServer.Services;

/// <summary>
/// playerId → StayConnected 채널. NotifyMailUpdated 등 다른 요청 핸들러가 이미 열려있는
/// StayConnected 스트림에 뭔가를 push해야 할 때 이걸 통해서만 쓴다(직접 스트림에 안 씀 -
/// 동시 쓰기 방지). ChatServer의 ChatConnectionRegistry와 같은 역할, 같은 이유.
/// </summary>
public sealed class LobbyConnectionRegistry
{
    private readonly ConcurrentDictionary<string, Channel<global::LobbyServer.StayConnectedEvent>> _channels = new();

    public void Register(string playerId, Channel<global::LobbyServer.StayConnectedEvent> channel) =>
        _channels[playerId] = channel;

    public void Unregister(string playerId) => _channels.TryRemove(playerId, out _);

    public bool TryGet(string playerId, out Channel<global::LobbyServer.StayConnectedEvent> channel) =>
        _channels.TryGetValue(playerId, out channel!);
}
