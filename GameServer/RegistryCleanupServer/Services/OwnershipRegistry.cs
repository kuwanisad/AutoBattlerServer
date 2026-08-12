using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Shared.Common.Redis;

namespace RegistryCleanupServer.Services;

/// <summary>
/// 서버별 관리 대상 명부와 GraveList를 관리한다.
/// </summary>
public sealed class OwnershipRegistry
{
    private readonly ISessionRegistry _sessionRegistry;
    private readonly ILogger<OwnershipRegistry> _logger;

    // serverId → 관리 대상 ID 목록
    private readonly ConcurrentDictionary<string, HashSet<string>> _registry = new();

    /// <summary>
    /// 죽은 서버의 ID 목록.
    /// 소유권 이전 요청(TransferOwnership) 시 to_server_id가 여기 있으면 거부한다.
    /// 이를 통해 이미 죽은 서버로의 이전을 막아 유령 상태를 방지한다.
    /// 서버 점검 시 초기화한다.
    /// </summary>
    private readonly HashSet<string> _graveList = new();

    private readonly object _lock = new();

    public OwnershipRegistry(ISessionRegistry sessionRegistry, ILogger<OwnershipRegistry> logger)
    {
        _sessionRegistry = sessionRegistry;
        _logger = logger;
    }

    /// <summary>관리 대상을 명부에 등록한다.</summary>
    public void Register(string serverId, string objectId)
    {
        lock (_lock)
        {
            if (!_registry.TryGetValue(serverId, out var objects))
            {
                objects = new HashSet<string>();
                _registry[serverId] = objects;
            }
            objects.Add(objectId);
        }

        _logger.LogDebug("명부 등록: {ServerId} → {ObjectId}", serverId, objectId);
    }

    /// <summary>관리 대상을 명부에서 해제한다.</summary>
    public void Unregister(string serverId, string objectId)
    {
        lock (_lock)
        {
            if (_registry.TryGetValue(serverId, out var objects))
                objects.Remove(objectId);
        }

        _logger.LogDebug("명부 해제: {ServerId} → {ObjectId}", serverId, objectId);
    }

    /// <summary>
    /// 소유권을 다른 서버로 이전한다.
    /// to_server_id가 GraveList에 있으면 거부한다.
    /// </summary>
    public bool TransferOwnership(string fromServerId, string toServerId, IEnumerable<string> objectIds)
    {
        lock (_lock)
        {
            if (_graveList.Contains(toServerId))
            {
                _logger.LogWarning("소유권 이전 거부 - 죽은 서버: {ToServerId}", toServerId);
                return false;
            }

            if (!_registry.TryGetValue(fromServerId, out var fromObjects))
                return true;

            if (!_registry.TryGetValue(toServerId, out var toObjects))
            {
                toObjects = new HashSet<string>();
                _registry[toServerId] = toObjects;
            }

            foreach (var objectId in objectIds)
            {
                fromObjects.Remove(objectId);
                toObjects.Add(objectId);
            }
        }

        _logger.LogInformation("소유권 이전: {From} → {To}", fromServerId, toServerId);
        return true;
    }

    /// <summary>서버 정상 종료 시 명부에서 제거한다.</summary>
    public int Shutdown(string serverId)
    {
        lock (_lock)
        {
            if (_registry.TryRemove(serverId, out var objects))
            {
                _logger.LogInformation("정상 종료 해제: {ServerId} ({Count}개)", serverId, objects.Count);
                return objects.Count;
            }
        }

        return 0;
    }

    /// <summary>
    /// 비정상 종료된 서버의 관리 대상을 일괄 정리한다.
    /// 해당 서버를 GraveList에 추가해 이후 소유권 이전 요청을 차단한다.
    /// </summary>
    public async Task<int> CleanupDeadServerAsync(string serverId)
    {
        HashSet<string> objects;

        lock (_lock)
        {
            _graveList.Add(serverId);

            if (!_registry.TryRemove(serverId, out var o))
            {
                _logger.LogWarning("명부에 없는 서버 죽음 통보: {ServerId}", serverId);
                return 0;
            }
            objects = o;
        }

        _logger.LogInformation("잔업 처리 시작: {ServerId} ({Count}개)", serverId, objects.Count);

        var tasks = objects.Select(async objectId =>
        {
            // 현재는 플레이어만 처리. 추후 타입별 분기 추가 예정.
            var session = await _sessionRegistry.GetAsync(objectId);
            if (session == null) return;

            switch (session.Status)
            {
                case Shared.Common.Redis.PlayerStatus.Matching:
                case Shared.Common.Redis.PlayerStatus.Moving:
                    session.Status = Shared.Common.Redis.PlayerStatus.Lobby;
                    session.CurrentServer = string.Empty;
                    session.NextServer = string.Empty;
                    session.MatchToken = string.Empty;
                    await _sessionRegistry.UpdateAsync(objectId, session);
                    break;

                case Shared.Common.Redis.PlayerStatus.Ingame:
                    session.Status = Shared.Common.Redis.PlayerStatus.Moving;
                    session.CurrentServer = string.Empty;
                    session.NextServer = "lobby";
                    await _sessionRegistry.UpdateAsync(objectId, session);
                    break;
            }
        });

        await Task.WhenAll(tasks);

        _logger.LogInformation("잔업 처리 완료: {ServerId} ({Count}개)", serverId, objects.Count);
        return objects.Count;
    }

    /// <summary>GraveList를 초기화한다. 서버 점검 시 호출한다.</summary>
    public void ClearGraveList()
    {
        lock (_lock)
        {
            var count = _graveList.Count;
            _graveList.Clear();
            _logger.LogInformation("GraveList 초기화: {Count}개 제거", count);
        }
    }
}
