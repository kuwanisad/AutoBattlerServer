using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Shared.Common.Messaging;

public static class RabbitMqServiceCollectionExtensions
{
    /// <summary>
    /// RabbitMQ 중개 클래스(RabbitMqBroker)를 등록한다 - "RabbitMQ" 설정 섹션을 바인딩하고, 싱글톤 겸
    /// 백그라운드 서비스(연결 유지 루프)로 등록한다. 메인 서버/하위 서버 공통.
    /// </summary>
    public static IServiceCollection AddRabbitMqBroker(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<RabbitMqOptions>(configuration.GetSection(RabbitMqOptions.SectionName));
        services.AddSingleton<RabbitMqBroker>();
        services.AddHostedService(sp => sp.GetRequiredService<RabbitMqBroker>());
        return services;
    }
}
