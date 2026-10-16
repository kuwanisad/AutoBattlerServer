using GameServer.Common;
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
/// "판단 계층"(이 클래스)을 분리했다. 감지 계층은 LeaveMatch/HandleServerDisconnected 두
/// 진입점만 호출하면 되고, 실제 되돌리기는 각 PendingMatch가 스스로 들고 있는
/// CompensatingTransaction이 담당한다.
///
/// 매치 하나를 놓고 "확정 처리"와 "끊김 처리"가 동시에 경합할 수 있으므로(예: 매치가 막
/// 확정되려는 순간 클라가 끊기는 경우), 매치의 생성/선점(=제거)을 _matchLock으로 감싼
/// 원자적 연산(TryClaimMatch)으로 통일했다 - 두 경로 다 이걸 거치므로 어느 한쪽만 결과를
/// 결정하고 나머지는 자동으로 물러난다. 로비 서버 하나가 죽었을 때 그 소속 매치를 빠르게
/// 찾기 위한 _matchesByLobby 인덱스도 같은 락으로 함께 보호한다(로비 여러 개가 동시에
/// 죽어도 순서대로 직렬화됨).
///
/// 알림 정책: 매칭 서버가 로비에게 "매칭이 잡혔다"고 알리기 전(PrepareMatch 요청~응답
/// 단계)에 중단되면, 클라 입장에서 애초에 아무것도 알려준 적이 없으므로 조용히 대기열로
/// 복귀시킨다. 방이 실제로 열려서 로비에 확정을 알리는 시점(=NotifyLobbyConfirmed) 이후에
/// 중단되면, 이미 알려준 것을 취소해야 하므로 명시적으로 취소 통보를 보낸다.
/// </summary>
public sealed class MatchEngine : IDisposable
{
    private readonly MatchCallbackClient _callbackClient;
    private readonly ConnectedServerRegistry _connectedServers;
    private ISessionRegistry _sessionRegistry;
    private ILogger<MatchEngine> _logger;

    // 매칭 대기 중인 플레이어 목록: playerId → WaitingPlayer (Queued 단계)
    private readonly ConcurrentDictionary<string, WaitingPlayer> _playerPool = new();

    // 매치 관련 상태 전부(_matches, _playerToMatch, _matchesByLobby)는 이 락으로 보호한다.
    // 개별 ConcurrentDictionary의 원자성만으로는 "여러 딕셔너리에 걸친 생성/삭제"의
    // 원자성을 보장 못 하므로, 그 상위 단위(매치 하나의 생성/선점)를 락으로 감쌌다.
    private readonly object _matchLock = new();

    // 진행 중인 매치: matchId → PendingMatch (PreparingRoom/Crossing 단계)
    private readonly Dictionary<string, PendingMatch> _matches = new();

    // playerId → matchId 역인덱스. 접속 끊긴 플레이어가 "어느 매치 소속인지" 바로 찾기 위함.
    private readonly Dictionary<string, string> _playerToMatch = new();

    // lobbyServerId → 그 로비 소속 플레이어가 하나라도 있는 matchId 목록.
    // 로비 하나가 죽었을 때 전체 매치를 스캔하지 않고 바로 찾기 위한 인덱스.
    private readonly Dictionary<string, HashSet<string>> _matchesByLobby = new();

    private readonly CancellationTokenSource _cts = new();

    public MatchEngine(
        MatchCallbackClient callbackClient,
        ConnectedServerRegistry connectedServers,
        ISessionRegistry sessionRegistry,
        ILogger<MatchEngine> logger)
    {
        _callbackClient = callbackClient;
        _connectedServers = connectedServers;
        _sessionRegistry = sessionRegistry;
        _logger = logger;

        // 매칭 루프 시작
        _ = RunMatchLoopAsync(_cts.Token);
    }

    /// <summary>대기열에 넣는다. 성공하면 null, 실패하면 그 사유를 돌려준다(로비가 클라에 MatchFailed로 전달).</summary>
    public MatchFailReason? JoinMatch(string playerId, string lobbyServerId, int skillScore, GameServer.Common.GameType gameType)
    {
        // 규칙이 없는 GameType(Unspecified/None/Deleted 포함)은 받지 않는다 - 받으면 매칭될 상대도
        // 방도 없어서 타임아웃까지 기다리다 실패할 뿐이다.
        if (!MatchingConstants.TryGetRule(gameType, out _))
        {
            _logger.LogWarning("매칭 대기 거부 - 지원하지 않는 GameType: {PlayerId} GameType={GameType}", playerId, gameType);
            return MatchFailReason.UnsupportedGameType;
        }

        var player = new WaitingPlayer
        {
            PlayerId = playerId,
            LobbyServerId = lobbyServerId,
            SkillScore = skillScore,
            GameType = gameType,
            JoinedAtUtc = DateTime.UtcNow,
        };

        if (!_playerPool.TryAdd(playerId, player))
        {
            _logger.LogWarning("이미 대기 중인 플레이어: {PlayerId}", playerId);
            return MatchFailReason.AlreadyQueued;
        }

        _logger.LogInformation("매칭 대기 진입: {PlayerId} GameType={GameType}", playerId, gameType);
        return null;
    }

    /// <summary>
    /// 클라 접속 끊김(로비가 감지 후 호출) 또는 명시적 매칭 취소 요청 - 둘 다 여기로 들어온다.
    /// 매치에 묶여있는지(PreparingRoom 이상)를 먼저 확인한다 - Queued 단계 체크보다 반드시
    /// 먼저 해야 한다(매치 소속 플레이어를 대기열 이탈로 오인하면 안 되므로).
    /// </summary>
    public bool LeaveMatch(string playerId)
    {
        if (_playerToMatch.TryGetValue(playerId, out var matchId) && TryClaimMatch(matchId, out var match))
        {
            _logger.LogInformation("매치 진행 중 플레이어 이탈: {PlayerId} (MatchId={MatchId})", playerId, matchId);
            DisruptMatch(match!, MatchDisruption.PlayerLeft(playerId));
            return true;
        }

        if (_playerPool.TryRemove(playerId, out _))
        {
            _logger.LogInformation("매칭 취소: {PlayerId}", playerId);
            return true;
        }

        _logger.LogWarning("대기 중이 아닌 플레이어 취소 요청: {PlayerId}", playerId);
        return false;
    }

    /// <summary>
    /// 게임 에이전트/로비 서버 접속이 끊겼을 때(MatchConnectionHandler가 감지) 호출한다.
    /// 그 서버가 관여하고 있던 것들을 찾아서 정리한다.
    /// </summary>
    public void HandleServerDisconnected(string serverId, GameServer.Common.ServerType serverType)
    {
        if (serverType == GameServer.Common.ServerType.Ingame)
        {
            List<PendingMatch> claimed = new();
            lock (_matchLock)
            {
                var affectedIds = _matches.Values
                    .Where(m => m.IngameServerId == serverId)
                    .Select(m => m.MatchId)
                    .ToList();

                foreach (var matchId in affectedIds)
                {
                    if (TryClaimMatchNoLock(matchId, out var match))
                        claimed.Add(match!);
                }
            }

            foreach (var match in claimed)
            {
                _logger.LogWarning("게임 에이전트 연결 끊김으로 매치 중단: {MatchId} (ServerId={ServerId})",
                    match.MatchId, serverId);
                DisruptMatch(match, MatchDisruption.IngameServerDisconnected(serverId));
            }
        }
        else if (serverType == GameServer.Common.ServerType.Lobby)
        {
            // Queued 단계 - 그 로비 소속 대기자 전부 제거 (매치에 안 묶여있으므로 되돌릴 것도 없음)
            var queuedPlayers = _playerPool.Values.Where(p => p.LobbyServerId == serverId).ToList();
            foreach (var player in queuedPlayers)
            {
                _playerPool.TryRemove(player.PlayerId, out _);
                _logger.LogWarning("로비 연결 끊김으로 대기열에서 제거: {PlayerId}", player.PlayerId);
            }

            // 매치에 묶인 단계 - 인덱스로 바로 찾는다 (전체 스캔 없음)
            List<PendingMatch> claimed = new();
            lock (_matchLock)
            {
                if (_matchesByLobby.TryGetValue(serverId, out var matchIds))
                {
                    foreach (var matchId in matchIds.ToList())
                    {
                        if (TryClaimMatchNoLock(matchId, out var match))
                            claimed.Add(match!);
                    }
                }
            }

            foreach (var match in claimed)
            {
                _logger.LogWarning("로비 연결 끊김으로 매치 중단: {MatchId} (LobbyServerId={ServerId})",
                    match.MatchId, serverId);
                DisruptMatch(match, MatchDisruption.LobbyDisconnected(serverId));
            }
        }
    }

    /// <summary>
    /// 매치를 원자적으로 선점(=제거)한다. 확정 처리(HandlePrepareMatchResultAsync)와 끊김 처리
    /// (LeaveMatch, HandleServerDisconnected) 둘 다 이걸 거친다 - 같은 matchId를 두고 경합하면
    /// 먼저 온 쪽만 성공하고 나머지는 false를 받아 조용히 물러난다.
    /// </summary>
    private bool TryClaimMatch(string matchId, out PendingMatch? match)
    {
        lock (_matchLock)
        {
            return TryClaimMatchNoLock(matchId, out match);
        }
    }

    /// <summary>_matchLock을 이미 잡은 상태에서만 호출한다 (여러 매치를 한 번에 순회하며 선점할 때 재진입 락 비용을 줄이기 위함).</summary>
    private bool TryClaimMatchNoLock(string matchId, out PendingMatch? match)
    {
        if (!_matches.Remove(matchId, out match))
        {
            return false;
        }

        foreach (var player in match.Players)
        {
            _playerToMatch.Remove(player.PlayerId);

            if (_matchesByLobby.TryGetValue(player.LobbyServerId, out var set))
            {
                set.Remove(matchId);
                if (set.Count == 0)
                {
                    _matchesByLobby.Remove(player.LobbyServerId);
                }
            }
        }

        return true;
    }

    private void RegisterMatch(PendingMatch match)
    {
        lock (_matchLock)
        {
            _matches[match.MatchId] = match;

            foreach (var player in match.Players)
            {
                _playerToMatch[player.PlayerId] = match.MatchId;

                if (!_matchesByLobby.TryGetValue(player.LobbyServerId, out var set))
                {
                    set = new HashSet<string>();
                    _matchesByLobby[player.LobbyServerId] = set;
                }
                set.Add(match.MatchId);
            }
        }
    }

    /// <summary>
    /// 선점된(=이미 _matches 등에서 제거된) 매치를 중단 처리한다. 지금까지 쌓인 되돌리기를
    /// 전부 실행하고, 생존자(끊긴 당사자가 아닌 나머지)를 대기열로 복귀시킨다.
    ///
    /// 누가 "끊긴 당사자"인지는 호출자가 아니라 여기서 중단 사유(disruption)를 보고 정한다 -
    /// 이탈한 플레이어, 죽은 로비 소속 플레이어는 대기열에도 넣지 않고 증발시킨다(넣으면 나중에
    /// 다시 매칭돼서, 받을 사람 없는 방이 게임 에이전트에 준비된다). 게임 에이전트 끊김과
    /// PrepareMatch 거부/실패는 플레이어 잘못이 아니므로 전원 복귀.
    ///
    /// 로비에 확정 통보(NotifyLobbyConfirmed)를 이미 보냈는지 여부로 조용히/시끄럽게를 가른다 -
    /// PreparingRoom 단계(확정 통보 전)면 조용히, Crossing 단계(확정 통보 후)면 취소 통보를
    /// 보내야 하지만, Crossing 단계 자체가 아직 이 코드베이스에 없어서(TODO) 지금은 항상
    /// PreparingRoom만 여기 들어온다 - 실제로 Crossing이 생기면 match.Phase로 분기 추가 필요.
    /// </summary>
    private void DisruptMatch(PendingMatch match, MatchDisruption disruption)
    {
        match.Transaction.Rollback();

        foreach (var player in match.Players)
        {
            if (disruption.IsDeparted(player))
            {
                _logger.LogInformation("매치 중단 - 끊긴 당사자라 대기열에 복귀시키지 않음: {PlayerId} (MatchId={MatchId}, {Disruption})",
                    player.PlayerId, match.MatchId, disruption);
                continue;
            }

            _playerPool.TryAdd(player.PlayerId, new WaitingPlayer
            {
                PlayerId = player.PlayerId,
                LobbyServerId = player.LobbyServerId,
                SkillScore = player.SkillScore,
                GameType = match.GameType, // 예전엔 빠져 있어서 복귀한 플레이어가 기본 게임 타입으로 다시 매칭됐다
                // 처음 대기열에 들어온 시각을 그대로 쓴다 - 예전엔 복귀할 때마다 지금 시각으로 갱신해서,
                // PrepareMatch가 계속 거부되면 시도→거부→복귀를 끝없이 반복하고 타임아웃이 안 걸렸다.
                JoinedAtUtc = player.JoinedAtUtc,
            });

            _logger.LogInformation("매치 중단으로 대기열 복귀: {PlayerId} (MatchId={MatchId}, {Disruption})",
                player.PlayerId, match.MatchId, disruption);
        }

        // TODO: match.Phase == Crossing이면 여기서 NotifyLobbyCancelled를 각 플레이어에게 보내야 함.
        // 지금은 Crossing 단계가 아직 안 쓰이므로(HandlePrepareMatchResultAsync가 성공 시 곧바로
        // 확정 처리하고 끝냄) 이 분기가 실제로 타지 않는다.
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
    private void NotifyLobbyCancelled(string playerId, string lobbyServerId, MatchFailReason reason, string detail = "")
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
                    Detail = detail,
                },
            },
        });
    }

    public Task HandlePrepareMatchResultAsync(string matchId, bool success, string errorMessage, string ingameAddress)
    {
        // 확정/끊김 경합의 관문 - 이미 다른 경로(접속 끊김 등)가 먼저 이 matchId를 선점했으면
        // 조용히 물러난다.
        if (!TryClaimMatch(matchId, out var match))
        {
            _logger.LogWarning("알 수 없거나 이미 처리된 matchId: {MatchId}", matchId);
            return Task.CompletedTask;
        }

        if (success)
        {
            // 여기가 "방이 열려서 로비에 알리는" 그 지점 - 이 시점 이후로 중단되면
            // (아직 미구현인 Crossing 단계) 반드시 취소 통보가 필요해진다.
            // ingameAddress는 게임 에이전트가 ServerReady로 미리 받아둔, 클라이언트가 실제로
            // NGO 접속할 주소다 - 서버 풀 등록 주소(게임 에이전트 자신의 gRPC 주소)와는 다르다.
            foreach (var player in match!.Players)
            {
                NotifyLobbyConfirmed(player.PlayerId, player.LobbyServerId, match.IngameServerId, ingameAddress);
            }
        }
        else
        {
            // PrepareMatch 자체가 실패한 경우 - 아직 로비에 "잡혔다"고 알린 적이 없으므로
            // (6번 이전) 조용히 대기열로 복귀시킨다. 취소 통보 없음.
            _logger.LogWarning("매치 준비 실패: {MatchId} - {Error}", matchId, errorMessage);
            DisruptMatch(match!, MatchDisruption.PrepareFailed());
        }

        return Task.CompletedTask;
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

                await Task.Delay(MatchingConstants.MatchLoopInterval, cancellationToken);
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

    /// <summary>
    /// GameType별로 대기열을 나눠 본다 - 같은 GameType끼리만, 그 GameType 규칙(MatchingConstants)의
    /// 정원만큼 모였을 때만 매치를 만든다(기본 규칙은 정원 충족 - 덜 모였으면 기다리고, 시간이 다
    /// 되면 CheckTimeouts가 타임아웃으로 실패 처리). 대기열 자체는 playerId로 찾는 _playerPool
    /// 하나를 GameType으로 묶어서 보는 방식이다 - 이탈/타임아웃/로비 끊김 처리가 playerId로
    /// 찾아야 해서, 대기열을 GameType별 자료구조로 따로 두면 두 곳을 같이 맞춰야 한다.
    /// GameType 하나당 루프 한 바퀴에 매치 하나씩 만든다.
    /// TODO: 실력 점수 기반 매칭 알고리즘으로 교체(지금은 먼저 온 순서).
    /// </summary>
    private async Task TryMatchAsync()
    {
        if (_playerPool.IsEmpty) return;

        var queues = _playerPool.Values
            .GroupBy(p => p.GameType)
            .ToList();

        foreach (var queue in queues)
        {
            if (!MatchingConstants.TryGetRule(queue.Key, out var rule)) continue; // JoinMatch가 이미 거르므로 이론상 없음
            if (queue.Count() < rule.RequiredPlayers) continue;

            var ingameServer = await FindAvailableIngameServerAsync();
            if (ingameServer == null) return; // 받을 인게임 서버가 없으면 다른 GameType도 못 잡는다

            var picked = TakeFromPool(queue.OrderBy(p => p.JoinedAtUtc), rule.RequiredPlayers);
            if (picked == null) continue; // 그 사이 이탈자가 생겨 정원이 깨짐 - 다음 바퀴에 다시

            await StartMatchAsync(queue.Key, picked, ingameServer);
        }
    }

    /// <summary>
    /// 대기열에서 앞에서부터 count명을 뺀다. 빼는 도중 이미 이탈한 사람이 있어서 count명을 못
    /// 채우면, 이번에 뺀 사람들을 원래대로 되돌리고 null을 돌려준다(정원 미달 매치를 만들지 않음).
    /// </summary>
    private List<WaitingPlayer>? TakeFromPool(IEnumerable<WaitingPlayer> candidates, int count)
    {
        var taken = new List<WaitingPlayer>(count);
        foreach (var candidate in candidates)
        {
            if (taken.Count == count) break;
            if (_playerPool.TryRemove(candidate.PlayerId, out var removed)) taken.Add(removed);
        }

        if (taken.Count == count) return taken;

        foreach (var player in taken) _playerPool.TryAdd(player.PlayerId, player);
        return null;
    }

    private async Task StartMatchAsync(
        GameServer.Common.GameType gameType, List<WaitingPlayer> players, ConnectedServerInfo ingameServer)
    {
        // 매치로 넘어가는 순간 대기열에서는 빠진다 - 로비 정보와 처음 대기열에 들어온 시각은
        // PendingMatch.Players에 스냅샷으로 들고 있으므로 더 이상 _playerPool을 참조할 필요가 없다.
        var matchId = Guid.NewGuid().ToString("N");

        var match = new PendingMatch
        {
            MatchId = matchId,
            Players = players.Select(p => new MatchPlayer
            {
                PlayerId = p.PlayerId,
                LobbyServerId = p.LobbyServerId,
                SkillScore = p.SkillScore,
                JoinedAtUtc = p.JoinedAtUtc,
            }).ToList(),
            IngameServerId = ingameServer.ServerId,
            CreatedAtUtc = DateTime.UtcNow,
            Phase = MatchPhase.PreparingRoom,
            GameType = gameType,
        };

        RegisterMatch(match);

        _logger.LogInformation("매치 시도: {MatchId} → {ServerId} (GameType={GameType}, {Count}명)",
            matchId, ingameServer.ServerId, gameType, match.Players.Count);

        // 게임 에이전트에 PrepareMatch 요청 (단항 - 요청/응답이 필요하므로 스트림으로 안 옮김)
        var playerIds = match.Players.Select(p => p.PlayerId).ToList();
        var accepted = await _callbackClient.PrepareMatchAsync(ingameServer.Address, matchId, playerIds, match.GameType);

        if (!accepted)
        {
            if (TryClaimMatch(matchId, out var claimedMatch))
            {
                _logger.LogWarning("PrepareMatch 거부: {MatchId}", matchId);
                DisruptMatch(claimedMatch!, MatchDisruption.PrepareRejected());
            }
            // TryClaimMatch가 실패했다면 그 사이 다른 경로(끊김)가 이미 정리한 것이므로 할 일 없음.
        }
        else
        {
            // PrepareMatch가 수락된 순간부터는 게임 에이전트 쪽에 실제로 방 생성 요청이
            // 나간 상태다. 이후 중단되면(끊김 등) 게임 에이전트한테 취소를 요청해야 한다.
            // CancelRoomAsync는 실제 gRPC 호출(I/O)이라 Rollback()이 그 완료까지 기다리게
            // 만들고 싶지 않아서 fire-and-forget으로 등록한다 - 실패해도 CancelRoomAsync
            // 내부에서 로그만 남기고 넘어가므로 여기서 별도 처리는 필요 없다.
            match.Transaction.OnStep(() =>
                _ = _callbackClient.CancelRoomAsync(ingameServer.Address, matchId, "매치 중단"));
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
        var timeout = MatchingConstants.MatchTimeout;
        var timedOut = _playerPool.Values
            .Where(p => DateTime.UtcNow - p.JoinedAtUtc > timeout)
            .ToList();

        foreach (var player in timedOut)
        {
            _playerPool.TryRemove(player.PlayerId, out _);
            _logger.LogInformation("매칭 타임아웃: {PlayerId}", player.PlayerId);

            NotifyLobbyCancelled(player.PlayerId, player.LobbyServerId, MatchFailReason.Timeout);
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _cts.Dispose();
    }
}

/// <summary>매치가 왜 중단됐는지 - DisruptMatch가 이걸 보고 누구를 대기열로 되돌릴지 정한다.</summary>
public enum MatchDisruptReason
{
    PlayerLeft,                // 플레이어가 이탈/매칭 취소(LeaveMatch) - 그 플레이어만 증발
    LobbyDisconnected,         // 로비 서버 연결 끊김 - 그 로비 소속 전원 증발
    IngameServerDisconnected,  // 게임 에이전트 연결 끊김 - 전원 복귀
    PrepareRejected,           // 게임 에이전트가 PrepareMatch를 거부 - 전원 복귀
    PrepareFailed,             // 방 준비 실패 응답 - 전원 복귀
}

/// <summary>
/// 중단 사유 + 그 사유의 대상 ID(이탈한 playerId, 끊긴 lobbyServerId, 끊긴 게임 에이전트 serverId).
/// 사유 열거형만으로는 "누가" 끊겼는지 알 수 없어서 대상 ID를 함께 묶었다 - 생성은 사유별
/// 팩토리로만 해서 사유와 ID 종류가 어긋나지 않게 한다.
/// </summary>
public readonly record struct MatchDisruption(MatchDisruptReason Reason, string SubjectId)
{
    public static MatchDisruption PlayerLeft(string playerId) => new(MatchDisruptReason.PlayerLeft, playerId);
    public static MatchDisruption LobbyDisconnected(string lobbyServerId) => new(MatchDisruptReason.LobbyDisconnected, lobbyServerId);
    public static MatchDisruption IngameServerDisconnected(string serverId) => new(MatchDisruptReason.IngameServerDisconnected, serverId);
    public static MatchDisruption PrepareRejected() => new(MatchDisruptReason.PrepareRejected, string.Empty);
    public static MatchDisruption PrepareFailed() => new(MatchDisruptReason.PrepareFailed, string.Empty);

    /// <summary>이 플레이어가 중단의 당사자라 대기열로 되돌리면 안 되는지.</summary>
    public bool IsDeparted(MatchPlayer player) => Reason switch
    {
        MatchDisruptReason.PlayerLeft => player.PlayerId == SubjectId,
        MatchDisruptReason.LobbyDisconnected => player.LobbyServerId == SubjectId,
        _ => false,
    };

    public override string ToString() =>
        string.IsNullOrEmpty(SubjectId) ? Reason.ToString() : $"{Reason}:{SubjectId}";
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
    public GameServer.Common.GameType GameType { get; set; }
    public DateTime JoinedAtUtc { get; set; }
}

/// <summary>매치에 소속된 플레이어 하나의 스냅샷. 생성 시점의 로비 정보를 그대로 들고 있어서,
/// 매치 관련 처리 중에 _playerPool을 다시 조회할 필요가 없다.</summary>
public sealed class MatchPlayer
{
    public string PlayerId { get; init; } = string.Empty;
    public string LobbyServerId { get; init; } = string.Empty;
    public int SkillScore { get; init; }

    /// <summary>처음 대기열에 들어온 시각 - 매치가 중단돼 대기열로 돌아갈 때 그대로 되살린다.</summary>
    public DateTime JoinedAtUtc { get; init; }
}

public sealed class PendingMatch
{
    public string MatchId { get; set; } = string.Empty;
    public List<MatchPlayer> Players { get; set; } = new();
    public string IngameServerId { get; set; } = string.Empty;
    public DateTime CreatedAtUtc { get; set; }
    public MatchPhase Phase { get; set; }
    public GameServer.Common.GameType GameType { get; set; }

    /// <summary>
    /// 이 매치가 지금까지 밟은 단계들의 되돌리기 스택. 상속이 아니라 조합(composition)으로
    /// 들고 있다 - CompensatingTransaction은 매칭 도메인과 무관한 범용 유틸이라서다.
    /// </summary>
    public CompensatingTransaction Transaction { get; } = new();
}