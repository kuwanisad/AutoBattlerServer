namespace SubServer.Common.Messaging;

/// <summary>Kafka 컨슈머 설정. appsettings.json의 "Kafka" 섹션에 매핑된다.</summary>
public sealed class KafkaConsumerOptions
{
    public const string SectionName = "Kafka";
    public string BootstrapServers { get; set; } = "localhost:9092";

    /// <summary>
    /// 컨슈머 그룹 ID. 같은 그룹에 속한 컨슈머들은 토픽의 파티션을 나눠서 처리한다.
    /// 하위 서버 종류별로 다르게 설정한다 (예: payment-service, reward-service).
    /// </summary>
    public string GroupId { get; set; } = string.Empty;

    /// <summary>구독할 토픽 목록.</summary>
    public List<string> Topics { get; set; } = new();

    /// <summary>"earliest": 처음부터, "latest": 연결 이후부터.</summary>
    public string AutoOffsetReset { get; set; } = "earliest";
}
