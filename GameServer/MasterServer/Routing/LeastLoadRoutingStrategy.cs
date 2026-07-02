using MasterServer.Registry;

namespace MasterServer.Routing;

/// <summary>
/// 여유 비율(= 1 - 현재부하/수용량)이 가장 높은 인게임 인스턴스를 고른다.
/// 이번 배정 인원만큼 여유가 없는 인스턴스는 후보에서 제외한다.
/// </summary>
public sealed class LeastLoadRoutingStrategy : IRoutingStrategy
{
    public InstanceInfo? SelectIngameInstance(IReadOnlyCollection<InstanceInfo> candidates, int partySize)
    {
        return candidates
            .Where(i => i.Capacity - i.CurrentLoad >= partySize)
            .OrderByDescending(i => (double)(i.Capacity - i.CurrentLoad) / Math.Max(i.Capacity, 1))
            .FirstOrDefault();
    }
}
