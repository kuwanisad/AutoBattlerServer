using Microsoft.Extensions.DependencyInjection;

namespace MainServer.Common.Messaging;

/// <summary>
/// 메인 서버(로비/인게임)가 Program.cs에서 한 줄로 Kafka/RabbitMQ 프로듀서를
/// 등록할 수 있게 해주는 확장 메서드.
///
/// shared.appsettings.json의 "Kafka", "RabbitMQ" 섹션을 자동으로 바인딩한다.
/// </summary>
public static class MainServerMessagingExtensions
{
    public static IServiceCollection AddMainServerMessaging(
        this IServiceCollection services,
        Microsoft.Extensions.Configuration.IConfiguration configuration)
    {
        services.Configure<KafkaProducerOptions>(
            configuration.GetSection(KafkaProducerOptions.SectionName));

        services.Configure<RabbitMqProducerOptions>(
            configuration.GetSection(RabbitMqProducerOptions.SectionName));

        // 프로듀서는 연결을 유지하므로 싱글톤으로 등록한다.
        // string 키, string 값이 기본 타입이며 필요 시 다른 타입으로 추가 등록 가능.
        services.AddSingleton<KafkaProducer<string, string>>();
        services.AddSingleton<RabbitMqProducer>();

        return services;
    }
}
