using Microsoft.Extensions.DependencyInjection;

namespace Shared.Common.Redis;

public static class SharedCommonExtensions
{
    /// <summary>
    /// Session Registry를 DI에 등록한다.
    /// 로비 서버, 게임 에이전트, 매칭 서버, 채팅 서버 등에서 호출한다.
    ///
    /// Redis 주소는 더 이상 로컬 설정(shared.appsettings.json)에서 읽지 않는다.
    /// 마스터 서버에 접속해서 주소를 받은 뒤 MasterConnectionService가
    /// ISessionRegistryConnector.ConnectAsync를 호출해야 실제로 연결된다.
    /// 그 전까지 ISessionRegistry를 호출하면 연결될 때까지 대기한다.
    /// </summary>
    public static IServiceCollection AddSessionRegistry(this IServiceCollection services)
    {
        services.AddSingleton<ISessionSerializer, JsonSessionSerializer>();
        services.AddSingleton<SessionRegistryConnector>();
        services.AddSingleton<ISessionRegistry>(sp => sp.GetRequiredService<SessionRegistryConnector>());
        services.AddSingleton<ISessionRegistryConnector>(sp => sp.GetRequiredService<SessionRegistryConnector>());
        services.AddSingleton<IRateLimiter>(sp => sp.GetRequiredService<SessionRegistryConnector>());
        return services;
    }
}
