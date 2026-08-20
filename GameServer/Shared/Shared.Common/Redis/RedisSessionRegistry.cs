using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace Shared.Common.Redis;

/// <summary>
/// Redis 기반 ISessionRegistry 구현체.
/// 플레이어 세션을 Redis String으로 저장한다.
/// 키 형식: session:{playerId}, server:{serverId}
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

    // session:{playerId}의 OwnerServerId를 확인해 권한 이전 여부를 원자적으로 판단하는 Lua 스크립트.
    // - 세션이 아예 없으면(최초 등록) 무조건 승인.
    // - 기존 소유자가 요청자 본인이면 승인 (idempotent).
    // - 기존 소유자의 TTL이 만료됐으면(서버 죽음) 승인.
    // - 그 외에는 거부.
    // 승인/거부 여부와 무관하게 "기존 소유자가 누구였는지"도 같이 반환한다
    // (거부됐을 때 호출자가 그 주소를 알아내는 데 씀).
    // 반환값: { claimed(1/0), previousOwnerServerId(문자열, 없으면 빈 문자열) }
    private const string TryClaimOwnershipScript = @"
        local sessionKey = KEYS[1]
        local newServerId = ARGV[1]
        local serializedSession = ARGV[2]
        local ttl = tonumber(ARGV[3])

        local existingJson = redis.call('GET', sessionKey)

        if not existingJson then
            redis.call('SET', sessionKey, serializedSession, 'EX', ttl)
            return {1, ''}
        end

        local currentOwner = string.match(existingJson, '""OwnerServerId"":""([^""]*)""')
        if not currentOwner then currentOwner = '' end

        if currentOwner == '' or currentOwner == newServerId then
            redis.call('SET', sessionKey, serializedSession, 'EX', ttl)
            return {1, currentOwner}
        end

        local ownerServerKey = 'server:' .. currentOwner
        local serverTtl = redis.call('TTL', ownerServerKey)

        -- TTL=-2: 키 없음(서버 죽음) → 권한 이전 승인
        if serverTtl == -2 then
            redis.call('SET', sessionKey, serializedSession, 'EX', ttl)
            return {1, currentOwner}
        end

        -- 아직 살아있으면 거부 (기존 세션은 건드리지 않음)
        return {0, currentOwner}
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

    // === 플레이어 세션 (조회/일반 갱신) ===

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

    // === 서버 등록/생존 ===

    public async Task RegisterServerAsync(string serverId, string address)
    {
        var key = ServerKeyPrefix + serverId;
        await _db.StringSetAsync(key, address, ServerHeartbeatConstants.Ttl);
        _logger.LogInformation("서버 등록: {ServerId} @ {Address}", serverId, address);
    }

    public async Task RefreshServerHeartbeatAsync(string serverId)
    {
        var key = ServerKeyPrefix + serverId;
        var refreshed = await _db.KeyExpireAsync(key, ServerHeartbeatConstants.Ttl);

        if (!refreshed)
        {
            // 키가 없다는 뜻 - RegisterServerAsync를 안 불렀거나, TTL이 이미 만료된 뒤라는 신호.
            _logger.LogWarning(
                "서버 하트비트 갱신 실패 - 등록되지 않은 서버(server:{ServerId}). RegisterServerAsync 호출 여부 확인 필요.",
                serverId);
        }
    }

    public async Task<bool> IsServerAliveAsync(string serverId)
    {
        var key = ServerKeyPrefix + serverId;
        return await _db.KeyExistsAsync(key);
    }

    public async Task<string?> GetServerAddressAsync(string serverId)
    {
        var key = ServerKeyPrefix + serverId;
        var value = await _db.StringGetAsync(key);
        return value.HasValue ? (string?)value : null;
    }

    // === 객체 권한 관리 ===

    public async Task<OwnerInfo?> GetOwnerAsync(string playerId)
    {
        var session = await GetAsync(playerId);
        if (session == null || string.IsNullOrEmpty(session.OwnerServerId))
        {
            return null;
        }

        var address = await GetServerAddressAsync(session.OwnerServerId);
        return new OwnerInfo(session.OwnerServerId, address, address != null);
    }

    public async Task<bool> TryClaimOwnershipAsync(string serverId, string playerId, PlayerSession newSession)
    {
        var result = await TryClaimOwnershipWithPreviousOwnerAsync(serverId, playerId, newSession);
        return result.Claimed;
    }

    public async Task<ClaimResult> TryClaimOwnershipWithPreviousOwnerAsync(
        string serverId, string playerId, PlayerSession newSession)
    {
        var sessionKey = SessionKeyPrefix + playerId;

        newSession.PlayerId = playerId;
        newSession.OwnerServerId = serverId;
        newSession.UpdatedAtUnixMs = TimeUtils.CurrentTimestamp();
        var serializedSession = _serializer.Serialize(newSession);

        var ttlSeconds = (long)DefaultTtl.TotalSeconds;

        var rawResult = await _db.ScriptEvaluateAsync(TryClaimOwnershipScript,
            keys: new RedisKey[] { sessionKey },
            values: new RedisValue[] { serverId, serializedSession, ttlSeconds });

        var result = (RedisResult[])rawResult!;
        var claimed = (int)result[0] == 1;
        var previousOwnerServerId = (string?)result[1];

        if (claimed)
        {
            _logger.LogInformation("권한 이전 승인: {PlayerId} → {ServerId}", playerId, serverId);
            return new ClaimResult(true, null);
        }

        _logger.LogWarning("권한 이전 거부: {PlayerId} (기존 서버 {Owner} 살아있음)", playerId, previousOwnerServerId);

        OwnerInfo? previousOwner = null;
        if (!string.IsNullOrEmpty(previousOwnerServerId))
        {
            var address = await GetServerAddressAsync(previousOwnerServerId);
            previousOwner = new OwnerInfo(previousOwnerServerId, address, address != null);
        }

        return new ClaimResult(false, previousOwner);
    }

    public void Dispose()
    {
        _redis?.Dispose();
    }
}