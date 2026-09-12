using Shared.Common.Endpoints;

namespace Shared.Common.Redis;

public sealed class RedisOptions
{
    public const string SectionName = "Redis";

    /// <summary>Redis 연결 문자열. 예: "localhost:6379"</summary>
    public string ConnectionString { get; set; } = InfraDefaults.RedisConnectionString;
}
