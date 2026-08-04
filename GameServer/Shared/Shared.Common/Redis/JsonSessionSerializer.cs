using System.Text.Json;

namespace Shared.Common.Redis;

/// <summary>
/// JSON 기반 PlayerSession 직렬화 구현체.
/// 가독성과 디버깅 편의를 위해 기본값으로 사용한다.
/// 성능이 병목이 되면 MessagePackSessionSerializer 등으로 교체한다.
/// </summary>
public sealed class JsonSessionSerializer : ISessionSerializer
{
    public string Serialize(PlayerSession session)
        => JsonSerializer.Serialize(session);

    public PlayerSession? Deserialize(string value)
        => JsonSerializer.Deserialize<PlayerSession>(value);
}
