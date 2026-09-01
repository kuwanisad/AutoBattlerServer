using Microsoft.Extensions.DependencyInjection;

namespace MainServer.Common.Messaging;

/// <summary>
/// 메인 서버(로비/인게임 등)가 Program.cs에서 한 줄로 Kafka/RabbitMQ 프로듀서/컨슈머를
/// 등록할 수 있게 해주는 확장 메서드.
///
/// shared.appsettings.json의 "Kafka", "RabbitMQ" 섹션을 자동으로 바인딩한다.
///
/// RabbitMqConsumer도 같이 등록해둔다 - 프로듀서만 있고 컨슈머가 없던 상태였는데(등록만
/// 되고 실제로 안 쓰이는 상태), 친구 삭제 → 채팅방 정리 같은 "다른 서버가 발행한 이벤트를
/// 받아야 하는" 상황이 생겨서 추가함. 프로듀서와 마찬가지로 실제로 Subscribe를 호출하는
/// 서버만 연결이 생기고, 안 쓰는 서버는 그냥 등록만 돼있고 비용이 없다.
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

        // 컨슈머는 Transient로 등록한다 - RabbitMqConsumer 하나가 "구독 하나"를 의미하고
        // 내부적으로 자기 채널을 인스턴스 필드에 들고 있어서, 같은 인스턴스를 여러 큐
        // 구독에 재사용하면 콜백이 엉뚱한 채널을 참조하게 되는 문제가 있다(서로 다른
        // 구독마다 반드시 별개 인스턴스가 필요함).
        services.AddTransient<RabbitMqConsumer>();

        return services;
    }
}
