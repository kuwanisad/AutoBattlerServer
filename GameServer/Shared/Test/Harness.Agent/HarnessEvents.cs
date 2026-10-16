using System.Text.Json;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Harness.Protocol;

namespace Harness.Agent;

/// <summary>
/// 테스트 하네스에 이벤트를 올려 보낸다. 하네스에 연결돼 있지 않으면(평소 실행, 끊김) 아무 일도
/// 안 한다 - 그래서 호출하는 쪽은 연결 여부를 신경 쓸 필요가 없다.
///
/// 이벤트는 판정과 타이밍 맞추기용이다(진단용은 로그). "꼭 필요한 순간"에만 의도해서 보낸다.
/// </summary>
public interface IHarnessEvents
{
    /// <param name="name">"&lt;영역&gt;.&lt;일&gt;" 소문자(예: "room.closed").</param>
    /// <param name="playerId">관련 플레이어가 있으면.</param>
    /// <param name="data">익명 객체 등 - JSON으로 직렬화된다.</param>
    void Emit(string name, string? playerId = null, object? data = null);
}

/// <summary>하네스 모드가 아닐 때 등록되는 빈 구현.</summary>
internal sealed class NullHarnessEvents : IHarnessEvents
{
    public void Emit(string name, string? playerId = null, object? data = null)
    {
    }
}

internal static class HarnessJson
{
    private static readonly JsonSerializerOptions Options = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public static Struct ToStruct(object? data)
    {
        if (data == null) return new Struct();
        return JsonParser.Default.Parse<Struct>(JsonSerializer.Serialize(data, Options));
    }

    public static AgentEvent CreateEvent(string name, string? playerId, object? data) => new()
    {
        TimestampUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        Name = name,
        PlayerId = playerId ?? "",
        Data = ToStruct(data),
    };
}
