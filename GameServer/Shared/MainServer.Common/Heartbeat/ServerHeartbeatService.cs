using MainServer.Common.MasterConnection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Shared.Common.Redis;

namespace MainServer.Common.Heartbeat;

/// <summary>
/// Redis(세션 레지스트리)에 이 서버 인스턴스의 생존 TTL을 주기적으로 갱신한다.
///
/// 예전에는 MasterConnectionService의 마스터 하트비트 루프 안에서 같이 처리했는데,
/// 세션 레지스트리 주소를 이제 마스터로부터 받아와야 하다 보니 순환 의존이 생겨서
/// (MasterConnectionService가 ISessionRegistry에 의존 → ISessionRegistry 연결은
/// MasterConnectionService가 주소를 받아야 시작 → ...) 별도 서비스로 분리했다.
///
/// ISessionRegistry(SessionRegistryConnector)가 첫 연결 전까지 호출을 대기시켜주므로,
/// 이 서비스는 마스터 접속/세션 레지스트리 연결 순서를 신경 쓰지 않고 그냥 시작하면 된다.
/// </summary>
public sealed class ServerHeartbeatService : BackgroundService
{
    private readonly ISessionRegistry _sessionRegistry;
    private readonly MasterConnectionOptions _options;
    private readonly ILogger<ServerHeartbeatService> _logger;

    public ServerHeartbeatService(
        ISessionRegistry sessionRegistry,
        IOptions<MasterConnectionOptions> options,
        ILogger<ServerHeartbeatService> logger)
    {
        _sessionRegistry = sessionRegistry;
        _options = options.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(ServerHeartbeatConstants.Interval, stoppingToken);
                await _sessionRegistry.RefreshServerHeartbeatAsync(_options.InstanceId);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                // 앱 종료 중 정상 취소
                break;
            }
            catch (Exception ex)
            {
                // 일시적인 Redis 장애일 수 있으니 루프를 끝내지 않고 다음 주기에 재시도한다.
                _logger.LogWarning(ex, "서버 생존 TTL 갱신 실패: {InstanceId} - 다음 주기에 재시도", _options.InstanceId);
            }
        }
    }
}
