using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Shared.Common.Redis;

public static class SharedCommonExtensions
{
    /// <summary>
    /// Session Registry를 DI에 등록한다.
    /// 로비 서버, 게임 에이전트, 매칭 서버 등에서 호출한다.
    /// </summary>
    public static IServiceCollection AddSessionRegistry(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.Configure<RedisOptions>(options =>
            configuration.GetSection(RedisOptions.SectionName).Bind(options));
        services.AddSingleton<ISessionSerializer, JsonSessionSerializer>();
        services.AddSingleton<ISessionRegistry, RedisSessionRegistry>();
        return services;
    }
}
