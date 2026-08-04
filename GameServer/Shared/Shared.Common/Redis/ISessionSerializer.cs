namespace Shared.Common.Redis;

/// <summary>
/// PlayerSession 직렬화/역직렬화 추상화.
/// 기본 구현은 JsonSessionSerializer이며,
/// 나중에 MessagePack, 비트 패킹 등으로 교체 시 이 인터페이스만 유지하면 된다.
/// </summary>
public interface ISessionSerializer
{
    string Serialize(PlayerSession session);
    PlayerSession? Deserialize(string value);
}
