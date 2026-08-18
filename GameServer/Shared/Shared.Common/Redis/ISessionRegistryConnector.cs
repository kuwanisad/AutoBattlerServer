namespace Shared.Common.Redis;

/// <summary>
/// 세션 레지스트리(Redis) 연결을 요청하는 인터페이스.
/// MasterConnectionService가 마스터로부터 주소를 받으면 이걸로 연결을 요청한다.
/// ISessionRegistry(CRUD 전체)와 분리해둔 이유는, MasterConnectionService가
/// "연결해라"는 것 외에 세션 CRUD까지 할 수 있게 만들 필요는 없기 때문이다.
/// </summary>
public interface ISessionRegistryConnector
{
    /// <summary>
    /// 주어진 주소로 Redis에 연결한다. 이미 같은 주소로 연결돼 있으면 아무 일도 하지 않는다.
    /// 다른 주소로 이미 연결돼 있으면 재연결한다 (마스터 재시작 등으로 주소가 바뀐 경우 대비).
    /// </summary>
    Task ConnectAsync(string address, CancellationToken cancellationToken = default);
}
