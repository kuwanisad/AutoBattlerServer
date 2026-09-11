using Microsoft.Extensions.Logging;

namespace Shared.Common.Redis;

/// <summary>
/// ISessionRegistry(CRUD)와 ISessionRegistryConnector(연결 요청)를 동시에 구현하는 프록시.
///
/// 마스터 서버에 접속해서 세션 레지스트리(Redis) 주소를 받기 전까지는 아직 Redis에
/// 연결돼 있지 않다. 이 프록시를 DI에 ISessionRegistry로 등록해두면, 실제로 Redis를
/// 쓰는 코드(LobbyControlService, MatchEngine, GameServerHandler, ChatChannelManager 등)는
/// 이 사실을 몰라도 된다 - 첫 연결 전에 호출하면 연결될 때까지 그냥 기다렸다가 처리될 뿐이다.
///
/// MasterConnectionService는 ISessionRegistryConnector만 주입받아서 ConnectAsync를 호출한다.
/// </summary>
public sealed class SessionRegistryConnector : ISessionRegistry, ISessionRegistryConnector, IRateLimiter, IDisposable
{
    private readonly ISessionSerializer _serializer;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<SessionRegistryConnector> _logger;

    private readonly object _lock = new();
    private TaskCompletionSource _firstConnectTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

    // 연결 완료 후에는 이 필드만 보고 바로 위임한다 (재연결 시에도 항상 최신 인스턴스).
    private volatile RedisSessionRegistry? _inner;
    private string? _connectedAddress;

    public SessionRegistryConnector(
        ISessionSerializer serializer,
        ILoggerFactory loggerFactory,
        ILogger<SessionRegistryConnector> logger)
    {
        _serializer = serializer;
        _loggerFactory = loggerFactory;
        _logger = logger;
    }

    public Task ConnectAsync(string address, CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            if (_connectedAddress == address && _inner != null)
            {
                return Task.CompletedTask;
            }

            _logger.LogInformation("세션 레지스트리 연결 요청: {Address}", address);

            var previous = _inner;
            var innerLogger = _loggerFactory.CreateLogger<RedisSessionRegistry>();
            _inner = new RedisSessionRegistry(address, _serializer, innerLogger);
            _connectedAddress = address;

            _firstConnectTcs.TrySetResult();

            previous?.Dispose();
        }

        return Task.CompletedTask;
    }

    private async Task<RedisSessionRegistry> GetRegistryAsync(CancellationToken cancellationToken = default)
    {
        var current = _inner;
        if (current != null) return current;

        await _firstConnectTcs.Task.WaitAsync(cancellationToken);
        return _inner!;
    }

    // === ISessionRegistry - 전부 연결될 때까지 기다렸다가 실제 구현체에 위임 ===

    public async Task<PlayerSession?> GetAsync(string playerId)
    {
        var registry = await GetRegistryAsync();
        return await registry.GetAsync(playerId);
    }

    public async Task UpdateAsync(string playerId, PlayerSession session)
    {
        var registry = await GetRegistryAsync();
        await registry.UpdateAsync(playerId, session);
    }

    public async Task RemoveAsync(string playerId)
    {
        var registry = await GetRegistryAsync();
        await registry.RemoveAsync(playerId);
    }

    public async Task<bool> ExistsAsync(string playerId)
    {
        var registry = await GetRegistryAsync();
        return await registry.ExistsAsync(playerId);
    }

    public async Task RegisterServerAsync(string serverId, string address)
    {
        var registry = await GetRegistryAsync();
        await registry.RegisterServerAsync(serverId, address);
    }

    public async Task RefreshServerHeartbeatAsync(string serverId, string address)
    {
        var registry = await GetRegistryAsync();
        await registry.RefreshServerHeartbeatAsync(serverId, address);
    }

    public async Task<bool> IsServerAliveAsync(string serverId)
    {
        var registry = await GetRegistryAsync();
        return await registry.IsServerAliveAsync(serverId);
    }

    public async Task<string?> GetServerAddressAsync(string serverId)
    {
        var registry = await GetRegistryAsync();
        return await registry.GetServerAddressAsync(serverId);
    }

    public async Task<OwnerInfo?> GetOwnerAsync(string playerId)
    {
        var registry = await GetRegistryAsync();
        return await registry.GetOwnerAsync(playerId);
    }

    public async Task<bool> TryClaimOwnershipAsync(string serverId, string playerId, PlayerSession newSession)
    {
        var registry = await GetRegistryAsync();
        return await registry.TryClaimOwnershipAsync(serverId, playerId, newSession);
    }

    public async Task<ClaimResult> TryClaimOwnershipWithPreviousOwnerAsync(
        string serverId, string playerId, PlayerSession newSession)
    {
        var registry = await GetRegistryAsync();
        return await registry.TryClaimOwnershipWithPreviousOwnerAsync(serverId, playerId, newSession);
    }

    // === IRateLimiter - 이미 있는 지연 연결을 그대로 재사용한다 (별도 연결 관리 불필요) ===

    public async Task<bool> TryAcquireAsync(string key, TimeSpan window)
    {
        var registry = await GetRegistryAsync();
        return await registry.TryAcquireAsync(key, window);
    }

    public void Dispose()
    {
        _inner?.Dispose();
    }
}