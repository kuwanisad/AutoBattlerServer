using Shared.Common.Endpoints;

namespace Shared.Common.Messaging;

/// <summary>
/// RabbitMQ 설정. shared.appsettings.json의 "RabbitMQ" 섹션에 매핑된다. 메인 서버와 하위 서버가
/// 같은 클래스를 쓴다(예전엔 RabbitMqProducerOptions/RabbitMqConsumerOptions에 접속 정보가 따로 있었음).
/// </summary>
public sealed class RabbitMqOptions
{
    public const string SectionName = "RabbitMQ";

    public string Host { get; set; } = InfraDefaults.RabbitMqHost;
    public int Port { get; set; } = InfraDefaults.RabbitMqPort;
    public string UserName { get; set; } = InfraDefaults.RabbitMqUserName;
    public string Password { get; set; } = InfraDefaults.RabbitMqPassword;
    public string VirtualHost { get; set; } = InfraDefaults.RabbitMqVirtualHost;

    /// <summary>접속 실패/끊김 시 다시 시도하는 간격(초).</summary>
    public int ReconnectIntervalSeconds { get; set; } = 5;

    /// <summary>발행 대기 버퍼의 기본 한도. -1이면 무한(기본값) - 잃으면 안 되는 메시지를 위해.</summary>
    public int DefaultBufferLimit { get; set; } = -1;

    /// <summary>
    /// 대상(큐 이름 또는 fanout exchange 이름)별 버퍼 한도. 채팅처럼 일부 유실이 괜찮고 최신이
    /// 중요한 대상만 여기에 적는다(넘치면 오래된 것부터 버림). 예: "BufferLimits": { "chat-relay": 1000 }
    /// </summary>
    public Dictionary<string, int> BufferLimits { get; set; } = new();

    public TimeSpan ReconnectInterval => TimeSpan.FromSeconds(Math.Max(1, ReconnectIntervalSeconds));

    public int GetBufferLimit(string target) =>
        BufferLimits.TryGetValue(target, out var limit) ? limit : DefaultBufferLimit;
}
