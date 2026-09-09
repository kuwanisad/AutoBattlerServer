using System.Collections.Concurrent;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace LobbyServer.Services;

/// <summary>
/// 공지처럼 "지금 접속 중인 전원 + 앞으로 새로 접속하는 클라 전부"한테 전달돼야 하는
/// 작업을 순서대로 처리한다. 정적일 필요는 없지만 인스턴스는 정확히 하나여야 한다
/// (여러 개면 각자 다른 Works/PendingWorks를 가져서 "하나의 순서로 직렬화"라는 목적이
/// 깨짐) - DI에서 AddSingleton + AddHostedService로 등록해서, 서버가 뜨는 순간부터
/// 끝날 때까지 하나만 존재하고 즉시 실행되도록 보장한다(사람이 "언제 만들지" 신경
/// 안 써도 됨 - Client의 MainThreadDispatcher와 같은 의도, 서버 환경에 맞는 방식으로
/// 구현).
///
/// 경합 방지 원칙(KNOWN_ISSUES.md 참고): "클라 접속 등록"과 "지속 업무 활성화 시
/// 전체 스냅샷"이 서로 겹치면, 그 찰나에 접속한 클라가 해당 업무를 영영 못 받는
/// 경합이 생긴다. 이걸 막으려면 두 작업이 항상 LobbyConnectionRegistry.SyncRoot
/// 락 안에서 순서대로 일어나야 한다 - 이 클래스 자체는 그 락을 모르고, 호출부
/// (LobbyControlService.EnterLobby, 공지 활성화 처리부)가 SyncRoot를 먼저 잡은
/// 채로 AddWork/ActivateStandingWork를 부르는 책임을 진다. 이 클래스 내부의
/// _standingLock은 그 SyncRoot 안쪽에서만 잡히므로(중첩 순서: SyncRoot → _standingLock),
/// 데드락 위험 없이 원자성이 보장된다.
/// </summary>
public sealed class GlobalWorker : BackgroundService
{
    private readonly LobbyConnectionRegistry _connectionRegistry;
    private readonly ILogger<GlobalWorker> _logger;

    // 지금 활성화된(계속 진행 중인) 지속 업무 - 새로 접속하는 클라한테 한 번에 다
    // 주기 위한 소스. AddWork(clientId)가 이걸 읽어서 그 클라 전용 배치를 만든다.
    private readonly HashSet<WorkItem> _standingWorkItems = new();
    private readonly object _standingLock = new();

    // 지금 처리 중인 배치. PendingWorks에서 다음 배치로 통째로 갈아 끼워진다.
    private ConcurrentDictionary<string, HashSet<WorkItem>> _works = new();
    private readonly ConcurrentQueue<ConcurrentDictionary<string, HashSet<WorkItem>>> _pendingWorks = new();
    private readonly SemaphoreSlim _signal = new(0);

    public GlobalWorker(LobbyConnectionRegistry connectionRegistry, ILogger<GlobalWorker> logger)
    {
        _connectionRegistry = connectionRegistry;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                await _signal.WaitAsync(stoppingToken);

                if (!_works.IsEmpty)
                {
                    foreach (var (clientId, items) in _works)
                    {
                        foreach (var item in items)
                        {
                            try
                            {
                                await DeliverAsync(clientId, item);
                            }
                            catch (Exception ex)
                            {
                                _logger.LogError(ex, "GlobalWorker 작업 전달 실패: clientId={ClientId} type={Type}",
                                    clientId, item.Type);
                            }
                        }
                    }
                }

                if (_pendingWorks.TryDequeue(out var next))
                {
                    _works = next;
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // 정상 종료
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "GlobalWorker 루프 중 예외 - 더 이상 작업을 처리하지 않음");
        }
    }

    /// <summary>클라 접속 시 호출 - 그 순간 활성화된 지속 업무 전부를 이 클라 전용
    /// 배치로 큐잉한다. 호출부가 LobbyConnectionRegistry.SyncRoot를 이미 잡고 있는
    /// 상태에서만 호출해야 한다.</summary>
    public void AddWork(string clientId)
    {
        HashSet<WorkItem> items;
        lock (_standingLock)
        {
            if (_standingWorkItems.Count == 0) return;
            items = new HashSet<WorkItem>(_standingWorkItems);
        }

        var batch = new ConcurrentDictionary<string, HashSet<WorkItem>>();
        batch[clientId] = items;
        _pendingWorks.Enqueue(batch);
        _signal.Release();
    }

    /// <summary>지속 업무를 활성 목록에 추가한다(방송은 안 함 - 방송은 EnqueueForClients로
    /// 별도 호출). 호출부가 SyncRoot를 이미 잡고 있는 상태에서 불러야 한다.</summary>
    public void ActivateStandingWork(WorkItem item)
    {
        lock (_standingLock)
        {
            _standingWorkItems.Add(item);
        }
    }

    /// <summary>지속 업무를 활성 목록에서 뺀다 - 앞으로 새로 접속하는 클라한테는 더
    /// 이상 안 준다(이미 받은 클라한테 "취소" 통지는 안 함 - 공지는 한 번 보면 그걸로
    /// 끝). 등록 순서와 무관한 단순 제거라 SyncRoot 없이 불러도 안전하다.</summary>
    public void DeactivateStandingWork(WorkItem item)
    {
        lock (_standingLock)
        {
            _standingWorkItems.Remove(item);
        }
    }

    /// <summary>주어진 클라 목록한테 이 작업을 큐잉한다. 호출부가 SyncRoot를 이미
    /// 잡은 채로 스냅샷을 뜬 직후 호출해야 한다(그래야 스냅샷과 이 큐잉 사이에 새
    /// 접속이 못 끼어듦).</summary>
    public void EnqueueForClients(IReadOnlyList<string> clientIds, WorkItem item)
    {
        if (clientIds.Count == 0) return;

        var batch = new ConcurrentDictionary<string, HashSet<WorkItem>>();
        foreach (var clientId in clientIds)
        {
            batch[clientId] = new HashSet<WorkItem> { item };
        }
        _pendingWorks.Enqueue(batch);
        _signal.Release();
    }

    /// <summary>실제 전달 로직 - WorkItem.Type에 따라 분기. 지금은 Announcement뿐.</summary>
    private async Task DeliverAsync(string clientId, WorkItem item)
    {
        switch (item.Type)
        {
            case WorkItemType.Announcement:
                if (_connectionRegistry.TryGet(clientId, out var channel))
                {
                    await channel.Writer.WriteAsync(new global::LobbyServer.StayConnectedEvent
                    {
                        Announcement = new global::LobbyServer.AnnouncementDelivery
                        {
                            AnnouncementType = (global::GameServer.Common.AnnouncementType)item.Payload,
                        },
                    });
                }
                break;

            default:
                _logger.LogWarning("GlobalWorker - 처리되지 않는 WorkItemType: {Type}", item.Type);
                break;
        }
    }
}
