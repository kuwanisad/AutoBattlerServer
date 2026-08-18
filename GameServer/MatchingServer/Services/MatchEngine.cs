using MatchingServer;
using Microsoft.Extensions.Logging;
using Shared.Common.Redis;
using System.Collections.Concurrent;
using System.Numerics;

namespace MatchingServer.Services;

/// <summary>
/// 플레이어 풀과 서버 풀을 관리하고 매칭 알고리즘을 실행한다.
/// 매칭 확정 시 MatchCallbackClient를 통해 로비/게임 에이전트에 통보한다.
/// </summary>
public sealed class MatchEngine : IDisposable
{
    private readonly MatchCallbackClient _callbackClient;
    private ISessionRegistry _sessionRegistry;
    private ILogger<MatchEngine> _logger;
    private readonly int _matchTimeoutSeconds;

    // 매칭 대기 중인 플레이어 목록: playerId → WaitingPlayer
    private readonly ConcurrentDictionary<string, WaitingPlayer> _playerPool = new();

    // 등록된 서버 목록: serverId → RegisteredServer
    private readonly ConcurrentDictionary<string, RegisteredServer> _serverPool = new();

    // 진행 중인 매치: matchId → PendingMatch
    private readonly ConcurrentDictionary<string, PendingMatch> _pendingMatches = new();

    private readonly CancellationTokenSource _cts = new();

    public MatchEngine(MatchCallbackClient callbackClient,
        ISessionRegistry sessionRegistry,
        ILogger<MatchEngine> logger,
        int matchTimeoutSeconds = 30)
    {
        _callbackClient = callbackClient;
        _sessionRegistry = sessionRegistry;
        _logger = logger;
        _matchTimeoutSeconds = matchTimeoutSeconds;

        // 매칭 루프 시작
        _ = RunMatchLoopAsync(_cts.Token);
    }

    public bool JoinMatch(string playerId, string lobbyServerId, int skillScore)
    {
        var player = new WaitingPlayer
        {
            PlayerId = playerId,
            LobbyServerId = lobbyServerId,
            SkillScore = skillScore,
            JoinedAtUtc = DateTime.UtcNow,
        };

        if (!_playerPool.TryAdd(playerId, player))
        {
            _logger.LogWarning("이미 대기 중인 플레이어: {PlayerId}", playerId);
            return false;
        }

        _logger.LogInformation("매칭 대기 진입: {PlayerId}", playerId);
        return true;
    }

    public bool LeaveMatch(string playerId)
    {
        if (_playerPool.TryRemove(playerId, out _) == false)
        {
            _logger.LogWarning("대기 중이 아닌 플레이어 취소 요청: {PlayerId}", playerId);
            return false;
        }

        _logger.LogInformation("매칭 취소: {PlayerId}", playerId);
        return true;
    }

    // === 서버 풀 관리 ===

    public void RegisterServer(string serverId, GameServer.Common.ServerType serverType, string address)
    {
        var server = new RegisteredServer
        {
            ServerId = serverId,
            ServerType = serverType,
            Address = address
        };

        // 재접속/재시작 시 주소가 바뀔 수 있으므로 있으면 갱신, 없으면 추가한다 (upsert).
        _serverPool.AddOrUpdate(serverId, server, (_, _) => server);

        _logger.LogInformation("서버 등록: {ServerId} ({ServerType}) @ {Address}",
            serverId, serverType, address);
    }

    public void UnregisterServer(string serverId)
    {
        if (_serverPool.TryRemove(serverId, out _))
        {
            _logger.LogInformation("서버 해제: {ServerId}", serverId);
        }
        else
        {
            _logger.LogInformation("서버 해제 실패 - 등록되지 않은 서버: {ServerId}", serverId);
        }
    }

    /// <summary>등록된 서버 풀에서 serverId(로비/게임 에이전트의 InstanceId)로 콜백 주소를 찾는다.</summary>
    private bool TryGetServerAddress(string serverId, out string address)
    {
        if (_serverPool.TryGetValue(serverId, out var server))
        {
            address = server.Address;
            return true;
        }

        address = string.Empty;
        return false;
    }

    /// <summary>매칭 확정을 로비 서버에 통보한다. 로비가 매칭 서버에 등록돼 있지 않으면(연결 끊김 등) 조용히 건너뛴다.</summary>
    private async Task NotifyLobbyConfirmedAsync(WaitingPlayer player, string ingameServerId, string ingameAddress)
    {
        if (!TryGetServerAddress(player.LobbyServerId, out var lobbyAddress))
        {
            _logger.LogWarning("매칭 확정 통보 실패 - 등록되지 않은 로비: {LobbyServerId} (Player={PlayerId})",
                player.LobbyServerId, player.PlayerId);
            return;
        }

        await _callbackClient.NotifyMatchConfirmedAsync(lobbyAddress, player.PlayerId, ingameServerId, ingameAddress);
    }

    /// <summary>매칭 취소를 로비 서버에 통보한다. 로비가 매칭 서버에 등록돼 있지 않으면(연결 끊김 등) 조용히 건너뛴다.</summary>
    private async Task NotifyLobbyCancelledAsync(WaitingPlayer player, string reason)
    {
        if (!TryGetServerAddress(player.LobbyServerId, out var lobbyAddress))
        {
            _logger.LogWarning("매칭 취소 통보 실패 - 등록되지 않은 로비: {LobbyServerId} (Player={PlayerId})",
                player.LobbyServerId, player.PlayerId);
            return;
        }

        await _callbackClient.NotifyMatchCancelledAsync(lobbyAddress, player.PlayerId, reason);
    }

    public async Task HandlePrepareMatchResultAsync(string matchId, bool success, string errorMessage)
    {
        if (_pendingMatches.TryRemove(matchId, out var match) == false)
        {
            _logger.LogWarning("알 수 없는 matchId: {MatchId}", matchId);
            return;
        }

        if (success)
        {
            List<WaitingPlayer> matchedPlayers = new();

            foreach (var playerId in match.PlayerIds)
            {
                if (!_playerPool.TryRemove(playerId, out var player))
                {
                    _logger.LogWarning("매치 취소 - 플레이어 풀에 없음: {PlayerId}", playerId);

                    // 이미 제거된 플레이어들에게 취소 통보
                    foreach (var pid in match.PlayerIds)
                    {
                        if (_playerPool.TryRemove(pid, out var p))
                        {
                            await NotifyLobbyCancelledAsync(p, "매치 구성 실패");
                        }
                    }
                    return;
                }

                matchedPlayers.Add(player);
            }

            // 전원 정상 제거 → 확정 통보
            foreach (var p in matchedPlayers)
            {
                await NotifyLobbyConfirmedAsync(p, match.IngameServerId, match.IngameAddress);
            }
        }
        else
        {
            _logger.LogWarning("매치 준비 실패: {MatchId} - {Error}", matchId, errorMessage);

            // 플레이어들 매칭 취소 통보
            foreach (var playerId in match.PlayerIds)
            {
                if (!_playerPool.TryRemove(playerId, out var player)) continue;

                await NotifyLobbyCancelledAsync(player, $"매치 준비 실패: {errorMessage}");
            }
        }
    }

    // === 매칭 루프 ===

    private async Task RunMatchLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (cancellationToken.IsCancellationRequested == false)
            {
                await TryMatchAsync();
                await CheckTimeoutsAsync();

                await Task.Delay(1000, cancellationToken);
            }
        }
        catch (OperationCanceledException e)
        {
            _logger.LogError(e, "매칭 루프 작업 취소");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "매칭 루프 오류");
        }
    }

    private async Task TryMatchAsync()
    {
        // 더미: 플레이어가 1명 이상이고 수용 가능한 인게임 서버가 있으면 매칭
        // TODO: 실력 점수 기반 매칭 알고리즘으로 교체
        var players = _playerPool.Values.ToList();
        if (players.Count == 0) return;

        var ingameServer = await FindAvailableIngameServerAsync();
        if (ingameServer == null) return;

        // 더미: 1명씩 매칭
        var player = players.First();
        var matchId = Guid.NewGuid().ToString("N");

        var match = new PendingMatch
        {
            MatchId = matchId,
            PlayerIds = new List<string> { player.PlayerId },
            IngameServerId = ingameServer.ServerId,
            IngameAddress = ingameServer.Address,
            CreatedAtUtc = DateTime.UtcNow,
        };

        _pendingMatches[matchId] = match;

        _logger.LogInformation("매치 시도: {MatchId} → {ServerId}", matchId, ingameServer.ServerId);

        // 게임 에이전트에 PrepareMatch 요청
        var accepted = await _callbackClient.PrepareMatchAsync(
            ingameServer.Address, matchId, match.PlayerIds);

        if (!accepted)
        {
            _pendingMatches.TryRemove(matchId, out _);
            _logger.LogWarning("PrepareMatch 거부: {MatchId}", matchId);
        }
    }

    private async Task<RegisteredServer?> FindAvailableIngameServerAsync()
    {
        foreach (var server in _serverPool.Values
            .Where(s => s.ServerType == GameServer.Common.ServerType.Ingame))
        {
            // Redis TTL로 서버 생존 확인
            if (await _sessionRegistry.IsServerAliveAsync(server.ServerId))
                return server;
        }
        return null;
    }

    private async Task CheckTimeoutsAsync()
    {
        var timeout = TimeSpan.FromSeconds(_matchTimeoutSeconds);
        var timedOut = _playerPool.Values.
            Where(p => DateTime.UtcNow - p.JoinedAtUtc > timeout).
            ToList();

        foreach (var player in timedOut)
        {
            _playerPool.TryRemove(player.PlayerId, out _);
            _logger.LogInformation("매칭 타임아웃: {PlayerId}", player.PlayerId);

            await NotifyLobbyCancelledAsync(player, "매칭 시간 초과");
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _cts.Dispose();
    }
}

public sealed class WaitingPlayer
{
    public string PlayerId { get; set; } = string.Empty;

    /// <summary>결과를 통보할 로비의 InstanceId. 실제 콜백 주소는 _serverPool에서 조회한다
    /// (로비 서버가 매칭 서버 주소 수신 시 RegisterServer로 자신을 등록해둔다).</summary>
    public string LobbyServerId { get; set; } = string.Empty;
    public int SkillScore { get; set; }
    public DateTime JoinedAtUtc { get; set; }
}

public sealed class RegisteredServer
{
    public string ServerId { get; set; } = string.Empty;
    public GameServer.Common.ServerType ServerType { get; set; }
    public string Address { get; set; } = string.Empty;
}

public sealed class PendingMatch
{
    public string MatchId { get; set; } = string.Empty;
    public List<string> PlayerIds { get; set; } = new();
    public string IngameServerId { get; set; } = string.Empty;
    public string IngameAddress { get; set; } = string.Empty;
    public DateTime CreatedAtUtc { get; set; }
}