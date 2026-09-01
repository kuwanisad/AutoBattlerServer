using MainServer.Common.MasterConnection;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace ChatServer;

/// <summary>
/// 채팅 서버 전용 Redis 연결. 세션 레지스트리(ISessionRegistry)와는 완전히 별개다 -
/// "채팅방을 세션 레지스트리에 등록하면 안 된다"는 원칙에 따라, 같은 물리적 Redis
/// 서버를 쓰더라도 완전히 다른 커넥션/키스페이스(`chat:`)로 접근한다.
///
/// SessionRegistryConnector와 같은 지연 연결 방식 - 마스터가 Redis 주소를 브로드캐스트할
/// 때까지 기다렸다가 연결한다.
/// </summary>
public sealed class ChatRedisConnection
{
    private readonly MasterConnectionService _masterConnection;
    private readonly ILogger<ChatRedisConnection> _logger;

    private ConnectionMultiplexer? _multiplexer;
    private readonly SemaphoreSlim _connectLock = new(1, 1);

    public ChatRedisConnection(MasterConnectionService masterConnection, ILogger<ChatRedisConnection> logger)
    {
        _masterConnection = masterConnection;
        _logger = logger;
    }

    public async Task<IDatabase> GetDatabaseAsync()
    {
        if (_multiplexer != null) return _multiplexer.GetDatabase();

        await _connectLock.WaitAsync();
        try
        {
            if (_multiplexer != null) return _multiplexer.GetDatabase();

            // 마스터가 Redis 주소를 브로드캐스트할 때까지 대기 (짧은 폴링 - 다른 지연
            // 연결 컴포넌트들과 같은 패턴).
            while (string.IsNullOrEmpty(_masterConnection.RedisAddress))
            {
                await Task.Delay(TimeSpan.FromMilliseconds(200));
            }

            _logger.LogInformation("채팅 서버용 Redis 연결 시도: {Address}", _masterConnection.RedisAddress);
            _multiplexer = await ConnectionMultiplexer.ConnectAsync(_masterConnection.RedisAddress);
            _logger.LogInformation("채팅 서버용 Redis 연결 완료");

            return _multiplexer.GetDatabase();
        }
        finally
        {
            _connectLock.Release();
        }
    }
}
