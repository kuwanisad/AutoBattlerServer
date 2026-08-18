using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace Shared.Common.Redis;

/// <summary>
/// Redis 기반 ISessionRegistry 구현체.
/// 플레이어 세션을 Redis String으로 저장한다.
/// 키 형식: session:{playerId}
/// 직렬화 방식은 ISessionSerializer에 위임한다.
///
/// 이 클래스는 순수하게 "이미 알고 있는 주소로 연결해서 CRUD를 수행"하는 역할만 한다.
/// 주소를 언제 어떻게 알아내는지(마스터로부터 수신 등)는 SessionRegistryConnector가 담당한다.
/// </summary>
public sealed class RedisSessionRegistry : ISessionRegistry, IDisposable
{
    private readonly IConnectionMultiplexer _redis;
    private readonly IDatabase _db;
    private readonly ISessionSerializer _serializer;
    private readonly ILogger _logger;

    private const string SessionKeyPrefix = "session:";
    private const string ServerKeyPrefix = "server:";

    private static readonly TimeSpan DefaultTtl = TimeSpan.FromHours(24);

    // session:{playerId}의 OwnerServerId를 확인해 권한 이전 여부를 원자적으로 판단하는 Lua 스크립트
    // 기존 owner 서버의 TTL이 만료됐으면 새 서버로 OwnerServerId를 교체 후 세션을 저장한다.
    private const string TryClaimOwnershipScript = @"
        local sessionKey = KEYS[1]
        local newServerId = ARGV[1]
        local serializedSession = ARGV[2]
        local ttl = tonumber(ARGV[3])

        local sessionJson = redis.call('GET', sessionKey)

        -- 세션이 없으면 거부
        if not sessionJson then
            return 0
        end

        -- OwnerServerId 파싱 (JSON에서 추출)
        local currentOwner = string.match(sessionJson, '""OwnerServerId"":""([^""]+)""')

        -- 기존 owner가 없으면 바로 승인
        if not currentOwner or currentOwner == '' then
            redis.call('SET', sessionKey, serializedSession, 'EX', ttl)
            return 1
        end

        -- 기존 owner 서버가 살아있는지 확인
        local ownerServerKey = 'server:' .. currentOwner
        local serverTtl = redis.call('TTL', ownerServerKey)

        -- TTL=-2: 키 없음(서버 죽음) → 권한 이전 승인
        if serverTtl == -2 then
            redis.call('SET', sessionKey, serializedSession, 'EX', ttl)
            return 1
        end

        -- 아직 살아있으면 거부
        return 0
    ";


    public RedisSessionRegistry(
        string connectionString,
        ISessionSerializer serializer,
        ILogger logger)
    {
        _serializer = serializer;
        _logger = logger;
        _redis = ConnectionMultiplexer.Connect(connectionString);
        _db = _redis.GetDatabase();
        _logger.LogInformation("Redis 연결 완료: {ConnectionString}", connectionString);
    }

    // === 플레이어 세션 ===

    public async Task RegisterAsync(string playerId, PlayerSession session)
    {
        session.PlayerId = playerId;
        session.UpdatedAtUnixMs = TimeUtils.CurrentTimestamp();

        var key = SessionKeyPrefix + playerId;
        var value = _serializer.Serialize(session);
        await _db.StringSetAsync(key, value, DefaultTtl);

        _logger.LogInformation("세션 등록: {PlayerId} → {Status}", playerId, session.Status);
    }

    public async Task<PlayerSession?> GetAsync(string playerId)
    {
        var key = SessionKeyPrefix + playerId;
        var value = await _db.StringGetAsync(key);

        if (!value.HasValue) return null;

        return _serializer.Deserialize(value!);
    }

    public async Task UpdateAsync(string playerId, PlayerSession session)
    {
        session.PlayerId = playerId;
        session.UpdatedAtUnixMs = TimeUtils.CurrentTimestamp();

        var key = SessionKeyPrefix + playerId;

        var value = _serializer.Serialize(session);
        await _db.StringSetAsync(key, value, DefaultTtl);

        _logger.LogInformation("세션 업데이트: {PlayerId} → {Status} @ {Server}",
            playerId, session.Status, session.CurrentServer);
    }

    public async Task RemoveAsync(string playerId)
    {
        var key = SessionKeyPrefix + playerId;
        await _db.KeyDeleteAsync(key);
        _logger.LogInformation("세션 삭제: {PlayerId}", playerId);
    }

    public async Task<bool> ExistsAsync(string playerId)
    {
        var key = SessionKeyPrefix + playerId;
        return await _db.KeyExistsAsync(key);
    }

    // === 서버 생존 TTL ===

    public async Task RefreshServerHeartbeatAsync(string serverId)
    {
        var key = ServerKeyPrefix + serverId;
        await _db.StringSetAsync(key, "1", ServerHeartbeatConstants.Ttl);
    }

    public async Task<bool> IsServerAliveAsync(string serverId)
    {
        var key = ServerKeyPrefix + serverId;
        return await _db.KeyExistsAsync(key);
    }

    // === 객체 권한 관리 ===

    public async Task<bool> TryClaimOwnershipAsync(string serverId, string playerId)
    {
        var sessionKey = SessionKeyPrefix + playerId;

        var session = await GetAsync(playerId);

        if (session == null)
        {
            return false;
        }

        session.OwnerServerId = serverId;
        session.UpdatedAtUnixMs = TimeUtils.CurrentTimestamp();
        var serializedSession = _serializer.Serialize(session);

        var ttl = await _db.KeyTimeToLiveAsync(sessionKey);
        var ttlSeconds = (long)DefaultTtl.TotalSeconds;

        var result = await _db.ScriptEvaluateAsync(TryClaimOwnershipScript,
            keys: new RedisKey[] { sessionKey },
            values: new RedisValue[] { serverId, serializedSession, ttlSeconds });

        var claimed = (int)result == 1;

        if (claimed)
            _logger.LogInformation("권한 이전 승인: {PlayerId} → {ServerId}", playerId, serverId);
        else
            _logger.LogWarning("권한 이전 거부: {PlayerId} (기존 서버 살아있음)", playerId);

        return claimed;
    }

    public void Dispose()
    {
        _redis.Dispose();
    }
}