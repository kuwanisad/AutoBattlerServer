using System.Text.Json;
using System.Text.Json.Nodes;
using Google.Protobuf;

namespace Harness.Agent.Fault;

/// <summary>규칙이 보는 메시지 방향 - 이 서버가 받는 쪽(request)이냐 내보내는 쪽(response, 스트림이면 메시지마다)이냐.</summary>
public enum FaultDirection
{
    Request,
    Response,
}

/// <summary>규칙에 걸린 메시지를 어떻게 하나.</summary>
public enum FaultActionKind
{
    /// <summary>JSON 경로의 값을 바꿔서 통과(내용 고치기).</summary>
    Rewrite,

    /// <summary>버린다. 요청이면 처리기가 안 돌고 상대는 응답 없이 기다린다(자기 제한 시간까지). 응답이면 서버는 일을 했는데 응답만
    /// 사라진다(스트림이면 그 메시지 하나만).</summary>
    Drop,

    /// <summary>delayMs 붙잡았다가 통과.</summary>
    Delay,

    /// <summary>gRPC 오류로 끝낸다(status). 요청이면 처리기가 안 돌고, 응답이면 서버는 일을 했는데 상대는 오류를 받는다(스트림이면 끊김).</summary>
    Fail,
}

/// <summary>
/// 경계 장애 규칙 하나(테스트 전용) - "이 서버의 어느 gRPC 메서드에서, 어느 방향 메시지를, 누구(playerId)의 것만, 어떤 내용일 때,
/// 어떻게(고침/버림/늦춤/실패), 몇 번 또는 얼마 동안". 게임 코드는 이 규칙을 모른다 - 프레임워크가 열어 둔 자리(인터셉터)에서만 건다.
/// </summary>
public sealed class FaultRule
{
    public required string Id { get; init; }

    /// <summary>gRPC 전체 메서드 이름(예: /LobbyControl/RequestMatch) 또는 "*"(전부).</summary>
    public required string Method { get; init; }

    public required FaultDirection Direction { get; init; }

    /// <summary>이 플레이어의 호출만(인증한 서버에서 - 인증 인터셉터가 남긴 UserState["playerId"]). null이면 전부.</summary>
    public string? PlayerId { get; init; }

    /// <summary>메시지(JSON, protobuf JSON 이름 - lowerCamelCase)가 이 조건을 다 만족할 때만. 경로 → 값 | {"$exists": bool} | {"$in": [...]}.</summary>
    public IReadOnlyDictionary<string, JsonElement> Match { get; init; } = new Dictionary<string, JsonElement>();

    public required FaultActionKind Action { get; init; }

    /// <summary>Rewrite: 경로 → 새 값.</summary>
    public IReadOnlyDictionary<string, JsonElement> Rewrite { get; init; } = new Dictionary<string, JsonElement>();

    public int DelayMs { get; init; }

    /// <summary>Fail: gRPC 상태 코드.</summary>
    public Grpc.Core.StatusCode FailStatus { get; init; } = Grpc.Core.StatusCode.Unavailable;

    /// <summary>남은 횟수(null = 무제한). 0이 되면 규칙이 끝난다.</summary>
    public int? Remaining { get; set; }

    /// <summary>끝나는 시각(null = 무기한).</summary>
    public DateTimeOffset? ExpiresAt { get; init; }

    public int Hits { get; set; }

    public object View() => new
    {
        ruleId = Id,
        method = Method,
        dir = Direction.ToString(),
        playerId = PlayerId,
        match = Match.Count > 0 ? Match : null,
        action = Action.ToString(),
        rewrite = Rewrite.Count > 0 ? Rewrite : null,
        delayMs = Action == FaultActionKind.Delay ? DelayMs : (int?)null,
        failStatus = Action == FaultActionKind.Fail ? FailStatus.ToString() : null,
        remaining = Remaining,
        expiresAtUtc = ExpiresAt,
        hits = Hits,
    };
}

/// <summary>메시지 하나에 규칙을 적용한 결과.</summary>
public readonly record struct FaultOutcome(FaultRule? Rule, IMessage Message)
{
    public bool Hit => Rule != null;
}

/// <summary>
/// 이 서버 프로세스의 경계 장애 규칙들(싱글턴) - 추가/삭제는 하네스 명령(fault.add/remove), 적용은 인터셉터(FaultInterceptor).
/// 메시지는 protobuf JSON으로 바꿔 경로로 비교하고 고친다 - 메시지 종류마다 코드를 쓰지 않는다. 걸릴 때마다 fault.hit, 규칙이 끝나면
/// (횟수/시간/삭제) fault.ended 이벤트(하네스 타임라인 - 지속 조작 추적 OverrideManager가 본다).
/// </summary>
public sealed class FaultRuleSet : IDisposable
{
    private static readonly JsonFormatter Formatter = new(new JsonFormatter.Settings(formatDefaultValues: true));
    private static readonly JsonParser Parser = new(JsonParser.Settings.Default.WithIgnoreUnknownFields(true));
    private const int PreviewChars = 400;

    private readonly object _lock = new();
    private readonly List<FaultRule> _rules = new();
    private readonly IHarnessEvents _events;
    private readonly Timer _expiry;
    private int _nextId;

    public FaultRuleSet(IHarnessEvents events)
    {
        _events = events;
        _expiry = new Timer(_ => ExpireDue(), null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
    }

    public void Dispose() => _expiry.Dispose();

    public bool IsEmpty
    {
        get { lock (_lock) return _rules.Count == 0; }
    }

    public FaultRule Add(Func<string, FaultRule> create)
    {
        lock (_lock)
        {
            var rule = create($"f{++_nextId}");
            _rules.Add(rule);
            return rule;
        }
    }

    public bool Remove(string ruleId, string reason = "removed")
    {
        FaultRule? rule;
        lock (_lock)
        {
            rule = _rules.FirstOrDefault(r => r.Id == ruleId);
            if (rule != null) _rules.Remove(rule);
        }
        if (rule != null) Ended(rule, reason);
        return rule != null;
    }

    public IReadOnlyList<object> List()
    {
        lock (_lock) return _rules.Select(r => r.View()).ToList();
    }

    /// <summary>
    /// 메시지 하나에 맞는 첫 규칙을 적용한다. 안 맞으면 Rule=null(그대로). 맞으면 횟수를 줄이고 fault.hit을 남긴다.
    /// Rewrite면 고친 메시지를 돌려준다(나머지 동작은 호출 쪽 - 인터셉터 - 이 처리).
    /// </summary>
    public FaultOutcome Apply(string method, FaultDirection direction, string? playerId, IMessage message)
    {
        if (IsEmpty) return new FaultOutcome(null, message);

        JsonObject? json = null;
        FaultRule? rule = null;
        var finished = false;
        lock (_lock)
        {
            foreach (var candidate in _rules)
            {
                if (candidate.Direction != direction) continue;
                if (candidate.Method != "*" && !string.Equals(candidate.Method, method, StringComparison.Ordinal)) continue;
                if (candidate.PlayerId != null && candidate.PlayerId != playerId) continue;
                if (candidate.ExpiresAt is { } expires && DateTimeOffset.UtcNow >= expires) continue;
                if (candidate.Match.Count > 0)
                {
                    json ??= ToJson(message);
                    if (!candidate.Match.All(m => Matches(json, m.Key, m.Value))) continue;
                }

                rule = candidate;
                rule.Hits++;
                if (rule.Remaining is { } remaining)
                {
                    rule.Remaining = remaining - 1;
                    if (rule.Remaining <= 0)
                    {
                        _rules.Remove(rule);
                        finished = true;
                    }
                }
                break;
            }
        }

        if (rule == null) return new FaultOutcome(null, message);

        var result = message;
        string? before = null;
        string? after = null;
        if (rule.Action == FaultActionKind.Rewrite)
        {
            json ??= ToJson(message);
            before = Preview(json.ToJsonString());
            foreach (var (path, value) in rule.Rewrite) Set(json, path, JsonNode.Parse(value.GetRawText()));
            after = Preview(json.ToJsonString());
            result = Parser.Parse(json.ToJsonString(), message.Descriptor);
        }
        else
        {
            before = Preview((json ?? ToJson(message)).ToJsonString());
        }

        _events.Emit("fault.hit", playerId, new
        {
            ruleId = rule.Id,
            method,
            dir = direction.ToString(),
            action = rule.Action.ToString(),
            before,
            after,
            hits = rule.Hits,
            remaining = rule.Remaining,
        });
        if (finished) Ended(rule, "times");
        return new FaultOutcome(rule, result);
    }

    /// <summary>경로가 메시지 형식에 있는지(규칙을 걸 때 오타를 막는다). 없으면 이유.</summary>
    public static string? CheckPath(Google.Protobuf.Reflection.MessageDescriptor descriptor, string path)
    {
        var current = descriptor;
        var parts = path.Split('.');
        for (var i = 0; i < parts.Length; i++)
        {
            var field = current.Fields.InDeclarationOrder().FirstOrDefault(f => f.JsonName == parts[i] || f.Name == parts[i]);
            if (field == null)
                return $"'{path}': {current.Name}에 '{parts[i]}' 필드가 없음(있는 것: {string.Join(", ", current.Fields.InDeclarationOrder().Select(f => f.JsonName))})";
            if (i < parts.Length - 1)
            {
                if (field.FieldType != Google.Protobuf.Reflection.FieldType.Message)
                    return $"'{path}': '{parts[i]}'는 메시지가 아니라 더 들어갈 수 없음";
                current = field.MessageType;
            }
        }
        return null;
    }

    private void ExpireDue()
    {
        List<FaultRule> expired;
        lock (_lock)
        {
            expired = _rules.Where(r => r.ExpiresAt is { } e && DateTimeOffset.UtcNow >= e).ToList();
            foreach (var rule in expired) _rules.Remove(rule);
        }
        foreach (var rule in expired) Ended(rule, "expired");
    }

    private void Ended(FaultRule rule, string reason) =>
        _events.Emit("fault.ended", rule.PlayerId, new { ruleId = rule.Id, reason, hits = rule.Hits, method = rule.Method, dir = rule.Direction.ToString() });

    private static JsonObject ToJson(IMessage message) => JsonNode.Parse(Formatter.Format(message))!.AsObject();

    private static string Preview(string text) => text.Length <= PreviewChars ? text : text[..PreviewChars] + "…";

    private static JsonNode? Get(JsonObject root, string path)
    {
        JsonNode? current = root;
        foreach (var part in path.Split('.'))
        {
            if (current is not JsonObject obj || !obj.TryGetPropertyValue(part, out current)) return null;
        }
        return current;
    }

    private static void Set(JsonObject root, string path, JsonNode? value)
    {
        var parts = path.Split('.');
        var current = root;
        for (var i = 0; i < parts.Length - 1; i++)
        {
            if (current[parts[i]] is not JsonObject next)
            {
                next = new JsonObject();
                current[parts[i]] = next;
            }
            current = next;
        }
        current[parts[^1]] = value;
    }

    private static bool Matches(JsonObject root, string path, JsonElement expected)
    {
        var actual = Get(root, path);
        if (expected.ValueKind == JsonValueKind.Object)
        {
            if (expected.TryGetProperty("$exists", out var exists))
                return (actual != null) == (exists.ValueKind == JsonValueKind.True);
            if (expected.TryGetProperty("$in", out var options) && options.ValueKind == JsonValueKind.Array)
                return actual != null && options.EnumerateArray().Any(o => Same(actual, o));
        }
        return actual != null && Same(actual, expected);
    }

    private static bool Same(JsonNode actual, JsonElement expected)
    {
        // protobuf JSON은 int64를 문자열로 낸다 - 값 비교는 문자열 표현으로(따옴표 제외).
        static string Plain(string raw) => raw.Length >= 2 && raw[0] == '"' && raw[^1] == '"' ? raw[1..^1] : raw;
        return Plain(actual.ToJsonString()) == Plain(expected.GetRawText());
    }
}
