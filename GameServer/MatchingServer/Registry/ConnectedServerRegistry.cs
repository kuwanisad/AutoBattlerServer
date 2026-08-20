using System.Collections.Concurrent;
using GameServer.Common;

namespace MatchingServer.Registry;

/// <summary>
/// 현재 매칭 서버에 접속 중인 로비/게임 에이전트 인스턴스를 추적하는 저장소.
/// MasterServer.Registry.InstanceRegistry와 같은 패턴이다.
///
/// 이 레지스트리가 RegisterServer/UnregisterServer 단항 RPC를 대체한다 - 접속(스트림 시작)이
/// 곧 등록이고, 접속 종료(스트림 끊김)가 곧 해제라서 별도 호출이 필요 없다.
/// </summary>
public sealed class ConnectedServerRegistry
{
    private readonly ConcurrentDictionary<string, ConnectedServerInfo> _servers = new();

    public ConnectedServerInfo Upsert(ConnectedServerInfo info)
    {
        return _servers.AddOrUpdate(
            info.ServerId,
            info,
            (_, existing) =>
            {
                existing.ServerType = info.ServerType;
                existing.Address = info.Address;
                existing.ConnectionToken = info.ConnectionToken;
                return existing;
            });
    }

    /// <summary>해당 serverId의 항목이 "지금도 내가 등록했던 그 연결"일 때만 제거한다 (재접속 경합 방지).</summary>
    public void RemoveIfCurrent(string serverId, Guid connectionToken)
    {
        if (_servers.TryGetValue(serverId, out var existing) && existing.ConnectionToken == connectionToken)
        {
            _servers.TryRemove(serverId, out _);
            existing.OutboundQueue.Writer.TryComplete();
        }
    }

    public IReadOnlyCollection<ConnectedServerInfo> GetByType(ServerType type) =>
        _servers.Values.Where(s => s.ServerType == type).ToList();

    public ConnectedServerInfo? TryGet(string serverId) =>
        _servers.TryGetValue(serverId, out var info) ? info : null;

    /// <summary>서버가 지금 이 레지스트리에 접속(생존)해 있는지. Redis TTL 대신 이걸로 즉시 확인 가능.</summary>
    public bool IsConnected(string serverId) => _servers.ContainsKey(serverId);

    /// <summary>특정 인스턴스 하나의 큐에 메시지를 넣는다. 없거나 큐가 닫혔으면 조용히 무시한다.</summary>
    public void Enqueue(string serverId, global::MatchingServer.MatchToServerMessage message)
    {
        if (_servers.TryGetValue(serverId, out var info))
        {
            info.OutboundQueue.Writer.TryWrite(message);
        }
    }
}
