using System.Collections.Concurrent;
using Grpc.Core;
using LobbyServer;
using MainServer.Common.MatchConnection;
using Microsoft.Extensions.Logging;
using Shared.Common;
using Shared.Common.Redis;

namespace LobbyServer.Services;

/// <summary>
/// 매칭 서버가 MatchServerConnection(Connect 스트림)으로 push하는 MatchNotification을
/// 받아서, 그 결과를 기다리고 있는 플레이어의 RequestMatch 스트림으로 전달한다.
///
/// 예전에는 LobbyControlService.NotifyMatch(단항 RPC)가 이 역할을 했는데, 매칭 서버가
/// 이제 로비 주소로 전화 걸지 않고 이미 열려있는 스트림에 실어 보내므로, 그 스트림의
/// 수신 루프(MatchServerConnection)가 이 디스패처를 호출하는 구조로 바뀌었다.
/// </summary>
public sealed class MatchNotificationDispatcher : IMatchNotificationHandler
{
    private readonly ISessionRegistry _sessionRegistry;
    private readonly LobbyMainServerIntermediary _mainServerIntermediary;
    private readonly ILogger<MatchNotificationDispatcher> _logger;

    // playerId → (스트림, CancellationTokenSource)
    // RequestMatch가 열린 스트림을 여기에 등록해두고 알림이 오면 찾아서 전달한다.
    private readonly ConcurrentDictionary<string, PendingMatch> _pendingStreams = new();

    public MatchNotificationDispatcher(
        ISessionRegistry sessionRegistry,
        LobbyMainServerIntermediary mainServerIntermediary,
        ILogger<MatchNotificationDispatcher> logger)
    {
        _sessionRegistry = sessionRegistry;
        _mainServerIntermediary = mainServerIntermediary;
        _logger = logger;
    }

    /// <summary>RequestMatch가 스트림을 열면서 결과를 기다릴 때 등록한다.</summary>
    public void Register(string playerId, IServerStreamWriter<MatchEvent> stream, CancellationTokenSource cts)
    {
        _pendingStreams[playerId] = new PendingMatch { Stream = stream, Cts = cts };
    }

    /// <summary>CancelMatch나 스트림 종료 시 등록을 해제한다. 대기 중이었으면 그 CTS를 반환한다.</summary>
    public bool TryUnregister(string playerId, out CancellationTokenSource? cts)
    {
        if (_pendingStreams.TryRemove(playerId, out var pending))
        {
            cts = pending.Cts;
            return true;
        }
        cts = null;
        return false;
    }

    public void HandleMatchNotification(global::MatchingServer.MatchNotification notification)
    {
        _ = HandleMatchNotificationSafeAsync(notification);
    }

    private async Task HandleMatchNotificationSafeAsync(global::MatchingServer.MatchNotification notification)
    {
        try
        {
            await HandleMatchNotificationAsync(notification);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "MatchNotification 처리 중 오류");
        }
    }

    private async Task HandleMatchNotificationAsync(global::MatchingServer.MatchNotification notification)
    {
        string playerId;
        bool isConfirmed;

        switch (notification.ResultCase)
        {
            case global::MatchingServer.MatchNotification.ResultOneofCase.Confirmed:
                playerId = notification.Confirmed.PlayerId;
                isConfirmed = true;
                break;
            case global::MatchingServer.MatchNotification.ResultOneofCase.Cancelled:
                playerId = notification.Cancelled.PlayerId;
                isConfirmed = false;
                break;
            default:
                _logger.LogWarning("알 수 없는 MatchNotification 종류: {Case}", notification.ResultCase);
                return;
        }

        if (!_pendingStreams.TryGetValue(playerId, out var pending))
        {
            _logger.LogWarning("매칭 알림 수신 - 대기 중인 스트림 없음: {PlayerId}", playerId);
            return;
        }

        if (isConfirmed)
        {
            var confirmed = notification.Confirmed;

            // 세션 Moving으로 업데이트 + OwnerServerId를 인게임 서버로 이전
            var session = await _sessionRegistry.GetAsync(playerId);
            if (session != null)
            {
                session.Status = PlayerStatus.Moving;
                session.NextServer = confirmed.IngameAddress;
                session.OwnerServerId = confirmed.IngameServerId;
                session.OwnerServerType = GameServer.Common.ServerType.Ingame;
                await _sessionRegistry.UpdateAsync(playerId, session);
            }

            // 일회용 임시 티켓 발급 + 게임 에이전트한테 미리 통지(KNOWN_ISSUES.md 참고) -
            // 여기선 매치 알림이 플레이어 개별로 오는 구조라 한 명짜리 배치로 부른다.
            // confirmed.IngameAddress는 클라가 NGO로 접속할 주소(스킴 없는 host:port)라
            // gRPC 채널 생성에 못 쓴다 - 게임 에이전트 자신의 gRPC 주소(SelfAddress,
            // 스킴 포함)를 세션 레지스트리에서 별도로 조회해야 한다.
            var ticket = string.Empty;
            var ingameGrpcAddress = await _sessionRegistry.GetServerAddressAsync(confirmed.IngameServerId);
            if (string.IsNullOrEmpty(ingameGrpcAddress))
            {
                _logger.LogWarning("게임 에이전트 gRPC 주소를 찾지 못함 - 전입 티켓 통지 생략: {PlayerId} ServerId={ServerId}",
                    playerId, confirmed.IngameServerId);
            }
            else
            {
                var tickets = await _mainServerIntermediary.IssueAndNotifyAsync(
                    new[] { playerId }, ingameGrpcAddress);
                ticket = tickets[playerId];
            }

            await pending.Stream.WriteAsync(new MatchEvent
            {
                MatchFound = new MatchFound
                {
                    MatchId = Guid.NewGuid().ToString("N"),
                    GameServerAddress = confirmed.IngameAddress,
                    IngameServerId = confirmed.IngameServerId,
                    TimestampUnixMs = TimeUtils.CurrentTimestamp(),
                    TransferTicket = ticket,
                },
            });

            _logger.LogInformation("매치 확정 전달: {PlayerId} → {Server}", playerId, confirmed.IngameAddress);
        }
        else
        {
            var session = await _sessionRegistry.GetAsync(playerId);
            if (session != null)
            {
                session.Status = PlayerStatus.Lobby;
                session.NextServer = string.Empty;
                await _sessionRegistry.UpdateAsync(playerId, session);
            }

            await pending.Stream.WriteAsync(new MatchEvent
            {
                MatchFailed = new MatchFailed { Reason = notification.Cancelled.Reason },
            });

            _logger.LogInformation("매칭 취소 전달: {PlayerId} ({Reason})", playerId, notification.Cancelled.Reason);
        }

        // 스트림 종료 (RequestMatch가 대기하던 Task.Delay를 풀어줌)
        pending.Cts.Cancel();
    }

    private sealed class PendingMatch
    {
        public IServerStreamWriter<MatchEvent> Stream { get; set; } = null!;
        public CancellationTokenSource Cts { get; set; } = null!;
    }
}