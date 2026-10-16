using Shared.Common.Endpoints;

namespace MainServer.Common.Messaging;

/// <summary>Kafka 프로듀서 설정. appsettings.json의 "Kafka" 섹션에 매핑된다.</summary>
public sealed class KafkaProducerOptions
{
    public const string SectionName = "Kafka";
    public string BootstrapServers { get; set; } = InfraDefaults.KafkaBootstrapServers;
}

// RabbitMQ 설정은 Shared.Common.Messaging.RabbitMqOptions로 옮김(메인/하위 서버 공통 중개 클래스 RabbitMqBroker).
