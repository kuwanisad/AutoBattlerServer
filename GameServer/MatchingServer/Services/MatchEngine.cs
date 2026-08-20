using MatchingServer;
using MatchingServer.Registry;
using Microsoft.Extensions.Logging;
using Shared.Common.Redis;
using Shared.Common.Transactions;
using System.Collections.Concurrent;

namespace MatchingServer.Services;

/// <summary>
/// 플레이어 풀을 관리하고 매칭 알고리즘을 실행한다.
/// 서버 목록(로비/게임 에이전트)은 더 이상 직접 들고 있지 않고 ConnectedServerRegistry를 참조한다 -
/// MatchConnectionControl.Connect 스트림 접속/해제가 곧 등록/해제이므로 별도로 관리할 필요가 없다.
/// 매칭 확정/취소 통보도 그 스트림(로비의 OutboundQueue)에 실어 보낸다 (예전엔 별도 unary 콜백이었음).
///
/// 접속 끊김 처리는 "감지 계층"(MatchConnectionHandler, LobbyMatchService 등)과
/// "판단 계층"(이 클래스)을 분리했다. 감지 계층은 HandlePlayerDisconnected/
/// HandleServerDisconnected 두 진입점만 호출하면 되고, "지금 몇 단계였고 뭘 되돌려야
/// 하는지"는 각 PendingMatch가 스스로 들고 있는 CompensatingTransaction이 기억한다.
/// </summary>
public sealed class MatchEngine : IDisposable
{
    private readonly MatchCallbackClient _callbackClient;
    private readonly ConnectedServerRegistry _connectedServers;
    private ISessionRegistry _sessionRegistry;
    private ILogger<MatchEngine> _logger;
    private readonly int _matchTimeoutSeconds;

    // 매칭 대기 중인 플레이어 목록: playerId → WaitingPlayer (Queued 단계)
    private readonly ConcurrentDictionary<string, WaitingPlayer> _playerPool = new();

    // 진행 중인 매치: matchId → PendingMatch (PreparingRoom/Crossing 단계)
    private readonly ConcurrentDictionary<string, PendingMatch> _matches = new();

    // playerId → matchId 역인덱스. 접속 끊긴 플레이어가 "어느 매치 소속인지" 바로 찾기 위함.
    private readonly ConcurrentDictionary<string, string> _playerToMatch = new();

    // 매치가 중단됐을 때 단계별로 뭘 해야 하는지 - 판단(정책)만 여기서 하고,
    // 실제 되돌리기 실행은 PendingMatch.Transaction.Rollback()에 위임한다.
    private readonly Dictionary<MatchPhase, Action<PendingMatch>> _onMatchDisrupted;

    private readonly CancellationTokenSource _cts = new();

    public MatchEngine(
        MatchCallbackClient callbackClient,
        ConnectedServerRegistry connectedServers,
        ISessionRegistry sessionRegistry,
        ILogger<MatchEngine> logger,
        int matchTimeoutSeconds = 30)
    {
        _callbackClient = callbackClient;
        _connectedServers = connectedServers;
        _sessionRegistry = sessionRegistry;
        _logger = logger;
        _matchTimeoutSeconds = matchTimeoutSeconds;

        _onMatchDisrupted = new Dictionary<MatchPhase, Action<PendingMatch>>
        {
            [MatchPhase.PreparingRoom] = DisruptPreparingRoom,

            // TODO: 게임 에이전트가 "플레이어가 실제로 접속 완료했다"를 매칭 서버에 보고하는
            // 경로가 아직 없어서(지금은 로비/세션 레지스트리만 갱신하고 끝) Crossing 단계
            // 자체를 아직 안 씀 - PendingMatch.Phase가 이 값이 되는 경우가 아직 없다.
            [MatchPhase.Crossing] = match =>
                _logger.LogError("Crossing 단계 처리 미구현 - MatchId={MatchId}", match.MatchId),
        };

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

    /// <summary>
    /// 클라 접속 끊김(로비가 감지 후 호출) 또는 명시적 매칭 취소 요청 - 둘 다 여기로 들어온다.
    /// 지금 이 플레이어가 Queued인지 PreparingRoom(매치에 묶여있는지)인지 확인해서 알맞게 처리한다.
    /// </summary>
    public bool LeaveMatch(string playerId)
    {
        // 1. Queued 단계였는지 먼저 확인 - 되돌릴 부작용이 없어서 그냥 빼면 끝.
        if (_playerPool.TryRemove(playerId, out _))
        {
            _logger.LogInformation("매칭 취소: {PlayerId}", playerId);
            return true;
        }

        // 2. PreparingRoom/Crossing 단계(매치에 묶여있는 중)였는지 확인.
        if (_playerToMatch.TryGetValue(playerId, out var matchId) &&
            _matches.TryGetValue(matchId, out var match))
        {
            _logger.LogInformation("매치 진행 중 플레이어 이탈: {PlayerId} (MatchId={MatchId})", playerId, matchId);
            _onMatchDisrupted[match.Phase](match);
            return true;
        }

        _logger.LogWarning("대기 중이 아닌 플레이어 취소 요청: {PlayerId}", playerId);
        return false;
    }

    /// <summary>
    /// 게임 에이전트/로비 서버 접속이 끊겼을 때(MatchConnectionHandler가 감지) 호출한다.
    /// 그 서버가 관여하고 있던 것들을 찾아서 단계별 정책대로 정리한다.
    /// </summary>
    public void HandleServerDisconnected(string serverId, GameServer.Common.ServerType serverType)
    {
        if (serverType == GameServer.Common.ServerType.Ingame)
        {
            var affected = _matches.Values.Where(m => m.IngameServerId == serverId).ToList();
            foreach (var match in affected)
            {
                _logger.LogWarning("게임 에이전트 연결 끊김으로 매치 중단: {MatchId} (ServerId={ServerId})",
                    match.MatchId, serverId);
                _onMatchDisrupted[match.Phase](match);
            }
        }
        else if (serverType == GameServer.Common.ServerType.Lobby)
        {
            // Queued 단계 - 그 로비 소속 대기자 전부 제거 (되돌릴 부작용 없음)
            var queuedPlayers = _playerPool.Values.Where(p => p.LobbyServerId == serverId).ToList();
            foreach (var player in queuedPlayers)
            {
                _playerPool.TryRemove(player.PlayerId, out _);
                _logger.LogWarning("로비 연결 끊김으로 대기열에서 제거: {PlayerId}", player.PlayerId);
            }

            // PreparingRoom 단계 - 그 로비 소속 플레이어가 하나라도 있는 매치는 전부 중단
            // (PreparingRoom 단계에선 플레이어가 아직 _playerPool에도 남아있으므로 LobbyServerId 조회 가능)
            var affectedMatchIds = _matches.Values
                .Where(m => m.PlayerIds.Any(pid =>
                    _playerPool.TryGetValue(pid, out var p) && p.LobbyServerId == serverId))
                .Select(m => m.MatchId)
                .Distinct()
                .ToList();

            foreach (var matchId in affectedMatchIds)
            {
                if (_matches.TryGetValue(matchId, out var match))
                {
                    _logger.LogWarning("로비 연결 끊김으로 매치 중단: {MatchId} (LobbyServerId={ServerId})",
                        match.MatchId, serverId);
                    _onMatchDisrupted[match.Phase](match);
                }
            }
        }
    }

    /// <summary>PreparingRoom 단계 중단 처리 - 매치를 되돌리고 남은 플레이어를 다시 대기열로.</summary>
    private void DisruptPreparingRoom(PendingMatch match)
    {
        // 아직 살아있는(=끊긴 당사자가 아닌) 플레이어들을 먼저 파악해둔다 - Rollback으로
        // _playerToMatch가 지워지기 전에 확보해야 한다.
        var remainingPlayerIds = match.PlayerIds
            .Where(pid => _playerPool.ContainsKey(pid))
            .ToList();

        match.Transaction.Rollback();

        foreach (var playerId in remainingPlayerIds)
        {
            _logger.LogInformation("매치 중단으로 대기열 복귀: {PlayerId} (MatchId={MatchId})",
                playerId, match.MatchId);
            // 이미 _playerPool에 있던 WaitingPlayer는 그대로 남아있으므로(TryMatchAsync가
            // PreparingRoom 단계에선 _playerPool에서 안 지웠음) 별도로 다시 넣을 필요가 없다 -
            // 그냥 매치 소속에서만 풀려나면 자동으로 다음 매칭 시도 대상이 된다.
        }
    }

    /// <summary>매칭 확정을 로비 서버에 통보한다. 로비가 접속돼 있지 않으면(연결 끊김 등) 조용히 건너뛴다.</summary>
    private void NotifyLobbyConfirmed(string playerId, string lobbyServerId, string ingameServerId, string ingameAddress)
    {
        if (!_connectedServers.IsConnected(lobbyServerId))
        {
            _logger.LogWarning("매칭 확정 통보 실패 - 접속 중이 아닌 로비: {LobbyServerId} (Player={PlayerId})",
                lobbyServerId, playerId);
            return;
        }

        _connectedServers.Enqueue(lobbyServerId, new MatchToServerMessage
        {
            MatchNotification = new MatchNotification
            {
                Confirmed = new MatchConfirmed
                {
                    PlayerId = playerId,
                    IngameServerId = ingameServerId,
                    IngameAddress = ingameAddress,
                },
            },
        });
    }

    /// <summary>매칭 취소를 로비 서버에 통보한다. 로비가 접속돼 있지 않으면(연결 끊김 등) 조용히 건너뛴다.</summary>
    private void NotifyLobbyCancelled(string playerId, string lobbyServerId, string reason)
    {
        if (!_connectedServers.IsConnected(lobbyServerId))
        {
            _logger.LogWarning("매칭 취소 통보 실패 - 접속 중이 아닌 로비: {LobbyServerId} (Player={PlayerId})",
                lobbyServerId, playerId);
            return;
        }

        _connectedServers.Enqueue(lobbyServerId, new MatchToServerMessage
        {
            MatchNotification = new MatchNotification
            {
                Cancelled = new MatchCancelled
                {
                    PlayerId = playerId,
                    Reason = reason,
                },
            },
        });
    }

    public Task HandlePrepareMatchResultAsync(string matchId, bool success, string errorMessage)
    {
        if (!_matches.TryGetValue(matchId, out var match))
        {
            _logger.LogWarning("알 수 없는 matchId: {MatchId}", matchId);
            return Task.CompletedTask;
        }

        if (success)
        {
            // TODO: Crossing 단계로 전이해야 하지만(match.Phase = MatchPhase.Crossing),
            // 아직 접속 완료 보고 경로가 없어서 지금은 예전처럼 여기서 바로 마무리한다.
            List<(string PlayerId, string LobbyServerId)> matchedPlayers = new();
            var allFound = true;

            foreach (var playerId in match.PlayerIds)
            {
                if (!_playerPool.TryGetValue(playerId, out var player))
                {
                    _logger.LogWarning("매치 취소 - 플레이어 풀에 없음: {PlayerId}", playerId);
                    allFound = false;
                    break;
                }

                matchedPlayers.Add((player.PlayerId, player.LobbyServerId));
            }

            if (!allFound)
            {
                DisruptPreparingRoomWithReason(match, "매치 구성 실패");
                return Task.CompletedTask;
            }

            foreach (var (playerId, _) in matchedPlayers)
            {
                _playerPool.TryRemove(playerId, out _);
                _playerToMatch.TryRemove(playerId, out _);
            }

            _matches.TryRemove(matchId, out _);

            foreach (var (playerId, lobbyServerId) in matchedPlayers)
            {
                NotifyLobbyConfirmed(playerId, lobbyServerId, match.IngameServerId, match.IngameAddress);
            }
        }
        else
        {
            _logger.LogWarning("매치 준비 실패: {MatchId} - {Error}", matchId, errorMessage);
            DisruptPreparingRoomWithReason(match, $"매치 준비 실패: {errorMessage}");
        }

        return Task.CompletedTask;
    }

    /// <summary>PrepareMatch 자체가 실패한 경우 - 중단이되 사유를 로비에 통보해야 하므로 별도 경로.</summary>
    private void DisruptPreparingRoomWithReason(PendingMatch match, string reason)
    {
        var players = match.PlayerIds
            .Where(pid => _playerPool.ContainsKey(pid))
            .Select(pid => (pid, LobbyServerId: _playerPool[pid].LobbyServerId))
            .ToList();

        match.Transaction.Rollback();

        foreach (var (playerId, lobbyServerId) in players)
        {
            NotifyLobbyCancelled(playerId, lobbyServerId, reason);
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
                CheckTimeouts();

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

        // 더미: 1명씩 매칭. 플레이어는 아직 _playerPool에 남겨둔다(PreparingRoom 단계에서도
        // LobbyServerId 조회가 필요해서) - HandlePrepareMatchResultAsync가 최종 성공/실패
        // 시점에 비로소 _playerPool에서 뺀다.
        var player = players.First();
        var matchId = Guid.NewGuid().ToString("N");

        var match = new PendingMatch
        {
            MatchId = matchId,
            PlayerIds = new List<string> { player.PlayerId },
            IngameServerId = ingameServer.ServerId,
            IngameAddress = ingameServer.Address,
            CreatedAtUtc = DateTime.UtcNow,
            Phase = MatchPhase.PreparingRoom,
        };

        _matches[matchId] = match;
        _playerToMatch[player.PlayerId] = matchId;

        match.Transaction.OnStep(() =>
        {
            _matches.TryRemove(matchId, out _);
            foreach (var pid in match.PlayerIds)
            {
                _playerToMatch.TryRemove(pid, out _);
            }
        });

        _logger.LogInformation("매치 시도: {MatchId} → {ServerId}", matchId, ingameServer.ServerId);

        // 게임 에이전트에 PrepareMatch 요청 (단항 - 요청/응답이 필요하므로 스트림으로 안 옮김)
        var accepted = await _callbackClient.PrepareMatchAsync(
            ingameServer.Address, matchId, match.PlayerIds);

        if (!accepted)
        {
            match.Transaction.Rollback();
            _logger.LogWarning("PrepareMatch 거부: {MatchId}", matchId);
        }
    }

    private async Task<ConnectedServerInfo?> FindAvailableIngameServerAsync()
    {
        foreach (var server in _connectedServers.GetByType(GameServer.Common.ServerType.Ingame))
        {
            // Redis TTL로 서버 생존 확인 (스트림이 살아있어도 부하 문제로 응답이 안 될 수 있으니
            // 이중 확인 차원 - 스트림 생존은 "접속돼 있다", TTL은 "최근까지 정상 동작했다"는 의미)
            if (await _sessionRegistry.IsServerAliveAsync(server.ServerId))
                return server;
        }
        return null;
    }

    private void CheckTimeouts()
    {
        var timeout = TimeSpan.FromSeconds(_matchTimeoutSeconds);
        var timedOut = _playerPool.Values
            .Where(p => !_playerToMatch.ContainsKey(p.PlayerId))  // 이미 매치에 묶인 플레이어는 제외
            .Where(p => DateTime.UtcNow - p.JoinedAtUtc > timeout)
            .ToList();

        foreach (var player in timedOut)
        {
            _playerPool.TryRemove(player.PlayerId, out _);
            _logger.LogInformation("매칭 타임아웃: {PlayerId}", player.PlayerId);

            NotifyLobbyCancelled(player.PlayerId, player.LobbyServerId, "매칭 시간 초과");
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _cts.Dispose();
    }
}

/// <summary>매치가 지금 어느 단계인지. 되돌릴 방법(Transaction)과 무관하게 "정책 선택"에만 쓴다.</summary>
public enum MatchPhase
{
    PreparingRoom,   // PrepareMatch 보내고 응답 기다리는 중
    Crossing,        // 방 열렸고 클라들이 NGO로 넘어가는 중 (TODO: 아직 감지 경로 없음)
}

public sealed class WaitingPlayer
{
    public string PlayerId { get; set; } = string.Empty;

    /// <summary>결과를 통보할 로비의 ServerId (ConnectedServerRegistry에서 찾음).</summary>
    public string LobbyServerId { get; set; } = string.Empty;
    public int SkillScore { get; set; }
    public DateTime JoinedAtUtc { get; set; }
}

public sealed class PendingMatch
{
    public string MatchId { get; set; } = string.Empty;
    public List<string> PlayerIds { get; set; } = new();
    public string IngameServerId { get; set; } = string.Empty;
    public string IngameAddress { get; set; } = string.Empty;
    public DateTime CreatedAtUtc { get; set; }
    public MatchPhase Phase { get; set; }

    /// <summary>
    /// 이 매치가 지금까지 밟은 단계들의 되돌리기 스택. 상속이 아니라 조합(composition)으로
    /// 들고 있다 - CompensatingTransaction은 매칭 도메인과 무관한 범용 유틸이라서다.
    /// </summary>
    public CompensatingTransaction Transaction { get; } = new();
}