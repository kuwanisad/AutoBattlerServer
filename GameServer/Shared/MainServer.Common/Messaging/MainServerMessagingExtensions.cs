using Microsoft.Extensions.DependencyInjection;
using Shared.Common.Messaging;

namespace MainServer.Common.Messaging;

/// <summary>
/// 메인 서버(로비/인게임 등)가 Program.cs에서 한 줄로 Kafka/RabbitMQ를 등록할 수 있게 해주는 확장 메서드.
///
/// shared.appsettings.json의 "Kafka", "RabbitMQ" 섹션을 자동으로 바인딩한다.
///
/// RabbitMQ는 발행/구독 모두 RabbitMqBroker 하나로 한다(Shared.Common.Messaging - 하위 서버와 공통).
/// 예전의 RabbitMqProducer(발행할 때마다 접속 시도, 실패하면 그 발행은 유실)와
/// RabbitMqConsumer(첫 접속 실패 시 예외 → 서버 시작 실패)를 대체한다 - 이제 접속은 백그라운드에서
/// 성공할 때까지 재시도하고, 연결이 없는 동안의 발행은 쌓아뒀다가 접속되면 보낸다.
/// 실제로 발행/구독을 하지 않는 서버도 접속은 유지한다(연결 하나 비용).
/// </summary>
public static class MainServerMessagingExtensions
{
    public static IServiceCollection AddMainServerMessaging(
        this IServiceCollection services,
        Microsoft.Extensions.Configuration.IConfiguration configuration)
    {
        services.Configure<KafkaProducerOptions>(
            configuration.GetSection(KafkaProducerOptions.SectionName));

        // Kafka 프로듀서는 연결을 유지하므로 싱글톤으로 등록한다(접속/재시도/버퍼는 Kafka 클라이언트
        // 라이브러리 자체가 갖고 있어서 따로 감싸지 않는다).
        // string 키, string 값이 기본 타입이며 필요 시 다른 타입으로 추가 등록 가능.
        services.AddSingleton<KafkaProducer<string, string>>();

        services.AddRabbitMqBroker(configuration);

        return services;
    }
}
