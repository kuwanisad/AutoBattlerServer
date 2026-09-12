using Shared.Common.Endpoints;

namespace MainServer.Common.Messaging;

/// <summary>Kafka 프로듀서 설정. appsettings.json의 "Kafka" 섹션에 매핑된다.</summary>
public sealed class KafkaProducerOptions
{
    public const string SectionName = "Kafka";
    public string BootstrapServers { get; set; } = InfraDefaults.KafkaBootstrapServers;
}

/// <summary>RabbitMQ 프로듀서 설정. appsettings.json의 "RabbitMQ" 섹션에 매핑된다.</summary>
public sealed class RabbitMqProducerOptions
{
    public const string SectionName = "RabbitMQ";
    public string Host { get; set; } = InfraDefaults.RabbitMqHost;
    public int Port { get; set; } = InfraDefaults.RabbitMqPort;
    public string UserName { get; set; } = InfraDefaults.RabbitMqUserName;
    public string Password { get; set; } = InfraDefaults.RabbitMqPassword;
    public string VirtualHost { get; set; } = InfraDefaults.RabbitMqVirtualHost;
}
