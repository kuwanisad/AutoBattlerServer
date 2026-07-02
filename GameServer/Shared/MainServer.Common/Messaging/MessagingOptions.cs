namespace MainServer.Common.Messaging;

/// <summary>Kafka 프로듀서 설정. appsettings.json의 "Kafka" 섹션에 매핑된다.</summary>
public sealed class KafkaProducerOptions
{
    public const string SectionName = "Kafka";
    public string BootstrapServers { get; set; } = "localhost:9092";
}

/// <summary>RabbitMQ 프로듀서 설정. appsettings.json의 "RabbitMQ" 섹션에 매핑된다.</summary>
public sealed class RabbitMqProducerOptions
{
    public const string SectionName = "RabbitMQ";
    public string Host { get; set; } = "localhost";
    public int Port { get; set; } = 5672;
    public string UserName { get; set; } = "guest";
    public string Password { get; set; } = "guest";
    public string VirtualHost { get; set; } = "/";
}
