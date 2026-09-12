using Shared.Common.Endpoints;

namespace SubServer.Common.Messaging;

/// <summary>RabbitMQ 컨슈머 설정. appsettings.json의 "RabbitMQ" 섹션에 매핑된다.</summary>
public sealed class RabbitMqConsumerOptions
{
    public const string SectionName = "RabbitMQ";
    public string Host { get; set; } = InfraDefaults.RabbitMqHost;
    public int Port { get; set; } = InfraDefaults.RabbitMqPort;
    public string UserName { get; set; } = InfraDefaults.RabbitMqUserName;
    public string Password { get; set; } = InfraDefaults.RabbitMqPassword;
    public string VirtualHost { get; set; } = InfraDefaults.RabbitMqVirtualHost;

    /// <summary>
    /// 구독할 큐 이름. work queue 패턴에서는 하위 서버 종류별로 전용 큐를 쓴다.
    /// fanout 브로드캐스트 수신 시엔 이 큐가 fanout exchange에 바인딩된다.
    /// </summary>
    public string QueueName { get; set; } = string.Empty;

    /// <summary>
    /// 바인딩할 exchange 이름. 비어있으면 기본 exchange(direct)를 쓴다.
    /// 운영 공지 같은 fanout 브로드캐스트를 받으려면 fanout exchange 이름을 지정한다.
    /// </summary>
    public string ExchangeName { get; set; } = string.Empty;

    /// <summary>한 번에 처리할 메시지 수. 처리 완료(Ack) 전까지 이 수만큼만 가져온다.</summary>
    public ushort PrefetchCount { get; set; } = 1;
}
