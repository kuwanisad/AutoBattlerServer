using System.Linq;
using MainServer.Common.MasterConnection;
using Microsoft.Extensions.Logging;

namespace LobbyServer.Services;

/// <summary>
/// MasterConnectionService.OnAnnouncementRequested를 구독해서 GlobalWorker한테
/// 실제 활성화/방송을 지시한다. Program.cs에서 AddSingleton으로 등록한 뒤 시작
/// 시점에 강제로 인스턴스화해야 구독이 걸린다(그냥 등록만 해두면 아무도 안 쓰니
/// 지연 생성되어 구독 자체가 안 걸림 - GetRequiredService로 시작 즉시 생성 필요).
/// </summary>
public sealed class LobbyAnnouncementHandler
{
    private readonly LobbyConnectionRegistry _connectionRegistry;
    private readonly GlobalWorker _globalWorker;
    private readonly ILogger<LobbyAnnouncementHandler> _logger;

    public LobbyAnnouncementHandler(
        MasterConnectionService masterConnection,
        LobbyConnectionRegistry connectionRegistry,
        GlobalWorker globalWorker,
        ILogger<LobbyAnnouncementHandler> logger)
    {
        _connectionRegistry = connectionRegistry;
        _globalWorker = globalWorker;
        _logger = logger;

        masterConnection.OnAnnouncementRequested += HandleAnnouncement;
    }

    private void HandleAnnouncement(global::MasterServer.AnnouncementTask task)
    {
        var item = new WorkItem(WorkItemType.Announcement, (int)task.AnnouncementType);

        if (task.Active)
        {
            // 활성화 + 지금 접속 중인 전원한테 방송을 한 락 안에서 원자적으로 처리한다 -
            // 안 그러면 그 찰나에 접속하는 클라가 EnterLobby 쪽 AddWork와 이 스냅샷
            // 어느 쪽에도 안 걸려서 공지를 영영 못 받는 경합이 생긴다.
            lock (_connectionRegistry.SyncRoot)
            {
                _globalWorker.ActivateStandingWork(item);
                var snapshot = _connectionRegistry.SnapshotPlayerIdsUnderLock();
                _globalWorker.EnqueueForClients(snapshot, item);
            }

            _logger.LogInformation("공지 활성화: {Type}", task.AnnouncementType);
        }
        else
        {
            // 새로 접속하는 클라를 향한 미래 동작만 바꾸는 거라(이미 받은 클라한테
            // "취소" 통지는 안 함) 등록 순서와 무관 - SyncRoot 불필요.
            _globalWorker.DeactivateStandingWork(item);
            _logger.LogInformation("공지 비활성화: {Type}", task.AnnouncementType);
        }
    }
}
