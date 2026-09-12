namespace Shared.Common.Endpoints;

/// <summary>
/// Redis/Kafka/RabbitMQ 등 브로커의 코드 기본 접속 정보를 한 곳에 모아둔다.
///
/// 예전엔 RedisOptions, (Main/SubServer 양쪽의) KafkaOptions, RabbitMqOptions 각각에
/// 같은 "localhost:포트" 값이 따로따로 하드코딩돼 있었다 - 값 자체는 항상
/// shared.appsettings.json이 덮어쓰므로(Shared/README.md 참고) 실제로 이 기본값이
/// 쓰일 일은 거의 없지만, 흩어진 중복을 없애기 위해 여기로 모았다.
/// </summary>
public static class InfraDefaults
{
    public const string RedisConnectionString = "localhost:6379";

    public const string KafkaBootstrapServers = "localhost:9092";

    public const string RabbitMqHost = "localhost";
    public const int RabbitMqPort = 5672;
    public const string RabbitMqUserName = "guest";
    public const string RabbitMqPassword = "guest";
    public const string RabbitMqVirtualHost = "/";
}
