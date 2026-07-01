using MasterServer.Registry;

namespace MasterServer.Routing;

/// <summary>
/// 로비가 매치를 확정했을 때 어느 인게임 인스턴스로 보낼지 결정하는 정책.
///
/// 지금은 단순한 정책 하나만 두지만, 이 인터페이스 뒤에 숨겨둔 이유는
/// 나중에 더 정교한 배치 정책(리전 고려, 워킹 셋 방식의 동적 분산 등)으로
/// 교체할 때 이 모듈 하나만 바꾸면 되게 하기 위해서다.
/// </summary>
public interface IRoutingStrategy
{
    /// <param name="candidates">현재 접속 중인 인게임 타입 인스턴스 후보들.</param>
    /// <param name="partySize">이번에 배정해야 할 인원 수.</param>
    /// <returns>선택된 인스턴스. 여유 있는 인스턴스가 없으면 null.</returns>
    InstanceInfo? SelectIngameInstance(IReadOnlyCollection<InstanceInfo> candidates, int partySize);
}
