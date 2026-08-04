using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace Shared.Common.Redis;

/// <summary>
/// Redis 기반 ISessionRegistry 구현체.
/// 플레이어 세션을 Redis String으로 저장한다.
/// 키 형식: session:{playerId}
/// 직렬화 방식은 ISessionSerializer에 위임한다.
/// </summary>
public sealed class RedisSessionRegistry : ISessionRegistry, IDisposable
{
    private readonly IConnectionMultiplexer _redis;
    private readonly IDatabase _db;
    private readonly ISessionSerializer _serializer;
    private readonly ILogger<RedisSessionRegistry> _logger;

    private const string KeyPrefix = "session:";
    private static readonly TimeSpan DefaultTtl = TimeSpan.FromHours(24);

    public RedisSessionRegistry(
        IOptions<RedisOptions> options,
        ISessionSerializer serializer,
        ILogger<RedisSessionRegistry> logger)
    {
        _serializer = serializer;
        _logger = logger;
        _redis = ConnectionMultiplexer.Connect(options.Value.ConnectionString);
        _db = _redis.GetDatabase();
        _logger.LogInformation("Redis 연결 완료: {ConnectionString}", options.Value.ConnectionString);
    }

    public async Task RegisterAsync(string playerId, PlayerSession session)
    {
        session.PlayerId = playerId;
        session.UpdatedAtUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        var key = KeyPrefix + playerId;
        var value = _serializer.Serialize(session);
        await _db.StringSetAsync(key, value, DefaultTtl);

        _logger.LogInformation("세션 등록: {PlayerId} → {Status}", playerId, session.Status);
    }

    public async Task<PlayerSession?> GetAsync(string playerId)
    {
        var key = KeyPrefix + playerId;
        var value = await _db.StringGetAsync(key);

        if (!value.HasValue) return null;

        return _serializer.Deserialize(value!);
    }

    public async Task UpdateAsync(string playerId, PlayerSession session)
    {
        session.PlayerId = playerId;
        session.UpdatedAtUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        var key = KeyPrefix + playerId;

        // TTL 유지하면서 값만 업데이트
        var ttl = await _db.KeyTimeToLiveAsync(key);
        var value = _serializer.Serialize(session);
        await _db.StringSetAsync(key, value, ttl ?? DefaultTtl);

        _logger.LogInformation("세션 업데이트: {PlayerId} → {Status} @ {Server}",
            playerId, session.Status, session.CurrentServer);
    }

    public async Task RemoveAsync(string playerId)
    {
        var key = KeyPrefix + playerId;
        await _db.KeyDeleteAsync(key);
        _logger.LogInformation("세션 삭제: {PlayerId}", playerId);
    }

    public async Task<bool> ExistsAsync(string playerId)
    {
        var key = KeyPrefix + playerId;
        return await _db.KeyExistsAsync(key);
    }

    public void Dispose()
    {
        _redis.Dispose();
    }
}
