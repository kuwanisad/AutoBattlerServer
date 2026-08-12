using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Shared.Common.Redis;

namespace RegistryCleanupServer.Services;

/// <summary>
/// 메인 서버별 담당 플레이어 명부를 관리한다.
/// 각 메인 서버는 자신이 담당하는 플레이어를 여기에 등록/해제한다.
/// 마스터 서버로부터 서버 죽음 통보 시 해당 서버의 플레이어들을 일괄 정리한다.
///
/// 명부는 메모리에 유지되며 서버 재시작 시 Redis에서 복원한다. (예정)
/// </summary>
public sealed class PlayerRegistry
{
    private readonly ISessionRegistry _sessionRegistry;
    private readonly ILogger<PlayerRegistry> _logger;

    // serverId → 담당 플레이어 목록
    private readonly ConcurrentDictionary<string, HashSet<string>> _registry = new();
    private readonly object _lock = new();

    public PlayerRegistry(ISessionRegistry sessionRegistry, ILogger<PlayerRegistry> logger)
    {
        _sessionRegistry = sessionRegistry;
        _logger = logger;
    }

    /// <summary>플레이어를 명부에 등록한다.</summary>
    public void Register(string serverId, string playerId)
    {
        lock (_lock)
        {
            if (!_registry.TryGetValue(serverId, out var players))
            {
                players = new HashSet<string>();
                _registry[serverId] = players;
            }
            players.Add(playerId);
        }

        _logger.LogDebug("명부 등록: {ServerId} → {PlayerId}", serverId, playerId);
    }

    /// <summary>플레이어를 명부에서 해제한다.</summary>
    public void Unregister(string serverId, string playerId)
    {
        lock (_lock)
        {
            if (_registry.TryGetValue(serverId, out var players))
                players.Remove(playerId);
        }

        _logger.LogDebug("명부 해제: {ServerId} → {PlayerId}", serverId, playerId);
    }

    /// <summary>서버가 정상 종료 시 해당 서버의 모든 플레이어를 해제한다.</summary>
    public int UnregisterServer(string serverId)
    {
        lock (_lock)
        {
            if (_registry.TryRemove(serverId, out var players))
            {
                _logger.LogInformation("서버 정상 종료 해제: {ServerId} ({Count}명)", serverId, players.Count);
                return players.Count;
            }
        }

        return 0;
    }

    /// <summary>
    /// 서버가 비정상 종료(죽음) 시 해당 서버의 플레이어 세션을 일괄 정리한다.
    /// </summary>
    public async Task<int> CleanupDeadServerAsync(string serverId)
    {
        HashSet<string> players;

        lock (_lock)
        {
            if (!_registry.TryRemove(serverId, out var p))
            {
                _logger.LogWarning("명부에 없는 서버 죽음 통보: {ServerId}", serverId);
                return 0;
            }
            players = p;
        }

        _logger.LogInformation("잔업 처리 시작: {ServerId} ({Count}명)", serverId, players.Count);

        var tasks = players.Select(async playerId =>
        {
            var session = await _sessionRegistry.GetAsync(playerId);
            if (session == null) return;

            // 상태에 따라 처리
            switch (session.Status)
            {
                case Shared.Common.Redis.PlayerStatus.Matching:
                case Shared.Common.Redis.PlayerStatus.Moving:
                    // 로비로 초기화
                    session.Status = Shared.Common.Redis.PlayerStatus.Lobby;
                    session.CurrentServer = string.Empty;
                    session.NextServer = string.Empty;
                    session.MatchToken = string.Empty;
                    await _sessionRegistry.UpdateAsync(playerId, session);
                    _logger.LogDebug("세션 초기화: {PlayerId} → Lobby", playerId);
                    break;

                case Shared.Common.Redis.PlayerStatus.Ingame:
                    // 인게임 중 서버 죽음 → Moving으로 표시 (재접속 정책에 따라 처리)
                    session.Status = Shared.Common.Redis.PlayerStatus.Moving;
                    session.CurrentServer = string.Empty;
                    session.NextServer = "lobby";
                    await _sessionRegistry.UpdateAsync(playerId, session);
                    _logger.LogDebug("세션 Moving: {PlayerId}", playerId);
                    break;

                default:
                    break;
            }
        });

        await Task.WhenAll(tasks);

        _logger.LogInformation("잔업 처리 완료: {ServerId} ({Count}명)", serverId, players.Count);
        return players.Count;
    }
}
