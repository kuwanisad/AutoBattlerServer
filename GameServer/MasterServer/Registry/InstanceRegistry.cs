using System.Collections.Concurrent;
using GameServer.Common;

namespace MasterServer.Registry;

/// <summary>
/// 현재 마스터 서버에 접속 중인 모든 메인 서버 인스턴스를 추적하는 저장소.
/// 인스턴스 수가 적으므로(수십~수백 단위, 용량 계획 기반) 인메모리로 충분하다.
/// 마스터 서버 프로세스가 재시작되면 모든 메인 서버가 다시 접속하면서 자연스럽게 다시 채워진다.
/// </summary>
public sealed class InstanceRegistry
{
    private readonly ConcurrentDictionary<string, InstanceInfo> _instances = new();

    public InstanceInfo Upsert(InstanceInfo info)
    {
        return _instances.AddOrUpdate(
            info.InstanceId,
            info,
            (_, existing) =>
            {
                existing.ServerType = info.ServerType;
                existing.Address = info.Address;
                existing.CurrentLoad = info.CurrentLoad;
                existing.Capacity = info.Capacity;
                existing.ConnectionToken = info.ConnectionToken;
                return existing;
            });
    }

    /// <summary>
    /// 해당 instanceId의 항목이 "지금도 내가 등록했던 그 연결"일 때만 제거한다.
    /// 부하로 정리가 지연된 옛 Connect 호출이, 그 사이 재접속한 새 연결의 항목을
    /// 실수로 지우는 것을 막기 위한 조건부 삭제다. 무조건 지우는 Remove(instanceId)는
    /// 더 이상 Connect 정리 경로에서 쓰지 않는다.
    /// </summary>
    public void RemoveIfCurrent(string instanceId, Guid connectionToken)
    {
        if (_instances.TryGetValue(instanceId, out var existing) && existing.ConnectionToken == connectionToken)
        {
            _instances.TryRemove(instanceId, out _);
            existing.OutboundQueue.Writer.TryComplete(); // 큐를 닫아 소비 루프가 자연스럽게 끝나게 한다
        }
    }

    public IReadOnlyCollection<InstanceInfo> GetByType(ServerType type) =>
        _instances.Values.Where(i => i.ServerType == type).ToList();

    public IReadOnlyCollection<InstanceInfo> GetAll() => _instances.Values.ToList();

    /// <summary>instanceId로 인스턴스를 직접 조회한다 (O(1)). GetAll().FirstOrDefault(...)로
    /// 순회하지 말고 이걸 쓴다 - 이미 딕셔너리로 관리하고 있으니 그 장점을 그대로 쓰는 것뿐이다.</summary>
    public InstanceInfo? TryGet(string instanceId) =>
        _instances.TryGetValue(instanceId, out var info) ? info : null;

    /// <summary>
    /// 특정 인스턴스 하나에 메시지를 큐에 넣는다. 실제 전송(WriteAsync)은 Connect의
    /// 소비 루프가 전담하므로 여기서는 그냥 큐에 넣기만 하면 된다 - 동시 쓰기 직렬화를
    /// 호출자가 신경 쓸 필요가 없다. 큐가 이미 닫혔다면(연결 종료) 조용히 무시한다.
    /// </summary>
    public void Enqueue(string instanceId, global::MasterServer.MasterMessage message)
    {
        if (_instances.TryGetValue(instanceId, out var info))
        {
            info.OutboundQueue.Writer.TryWrite(message); // Unbounded 채널이라 항상 즉시 성공하거나, 닫혔으면 false
        }
    }

    /// <summary>현재 접속 중인 모든 인스턴스의 큐에 같은 메시지를 넣는다 (운영 공지 릴레이 등에 사용).</summary>
    public void Broadcast(global::MasterServer.MasterMessage message)
    {
        foreach (var info in GetAll())
        {
            info.OutboundQueue.Writer.TryWrite(message);
        }
    }
}