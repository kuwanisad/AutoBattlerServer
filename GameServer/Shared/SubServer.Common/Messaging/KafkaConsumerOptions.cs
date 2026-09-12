using Shared.Common.Endpoints;

namespace SubServer.Common.Messaging;

/// <summary>Kafka 컨슈머 설정. appsettings.json의 "Kafka" 섹션에 매핑된다.</summary>
public sealed class KafkaConsumerOptions
{
    public const string SectionName = "Kafka";
    public string BootstrapServers { get; set; } = InfraDefaults.KafkaBootstrapServers;

    /// <summary>
    /// 컨슈머 그룹 ID. 같은 그룹에 속한 컨슈머들은 토픽의 파티션을 나눠서 처리한다.
    /// 하위 서버 종류별로 다르게 설정한다 (예: payment-service, reward-service).
    /// </summary>
    public string GroupId { get; set; } = string.Empty;

    /// <summary>구독할 토픽 목록.</summary>
    public List<string> Topics { get; set; } = new();

    /// <summary>"earliest": 처음부터, "latest": 연결 이후부터.</summary>
    public string AutoOffsetReset { get; set; } = "earliest";

    /// <summary>
    /// 메시지 폴링 주기. 하위 서버마다 실시간성 중요도가 다를 수 있어 설정으로 뺐다.
    /// 실시간성이 중요한 서버는 낮게(예: 10ms), 배치성 처리는 높게(예: 500ms) 설정한다.
    /// 기본값 100ms는 대부분의 경우에 적절한 균형점이다.
    /// </summary>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromMilliseconds(100);
}
