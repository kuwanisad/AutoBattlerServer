using System.Reflection;
using System.Text.Json;
using Google.Protobuf;
using Google.Protobuf.Reflection;
using Grpc.AspNetCore.Server;
using Grpc.Core;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Harness.Agent.Fault;

/// <summary>
/// 경계 장애 규칙 명령(영역 fault, 테스트 전용 - 모든 gRPC 서버 공통). 하네스는 이 명령을 agent_command로 보내고, 켜 둔 규칙은 하네스의
/// 지속 조작 추적(OverrideManager, 종류 fault)이 기록을 보고 수명(노드 끝/시나리오 끝)대로 fault.remove로 되돌린다.
/// </summary>
internal sealed class FaultCommands
{
    private readonly FaultRuleSet _rules;
    private readonly IServiceProvider _services;

    public FaultCommands(FaultRuleSet rules, IServiceProvider services)
    {
        _rules = rules;
        _services = services;
    }

    [HarnessCommand("fault.methods", Description = "이 서버가 받는 gRPC 메서드 목록(장애 규칙의 method 값): 이름, 종류(Unary/ServerStreaming ...), " +
                                                   "요청/응답 메시지와 필드 경로(protobuf JSON 이름 - match/rewrite에 쓰는 경로). query로 이름 거르기")]
    public object Methods(string? query = null)
    {
        var methods = GrpcMethods()
            .Where(m => string.IsNullOrEmpty(query) || m.Method.FullName.Contains(query, StringComparison.OrdinalIgnoreCase))
            .Select(m => new
            {
                method = m.Method.FullName,
                type = m.Method.Type.ToString(),
                request = Describe(m.Request),
                response = Describe(m.Response),
            })
            .ToList();
        return new { methods, count = methods.Count };
    }

    [HarnessCommand("fault.add", Description = "경계 장애 규칙을 건다(이 서버가 받는 gRPC 호출). method(전체 이름 또는 *), dir(request=들어오는 것 | " +
                                               "response=내보내는 것, 스트림은 메시지마다), action(rewrite | drop | delay | fail), playerId(그 플레이어 호출만 - 인증하는 서버에서), " +
                                               "match({경로: 값 | {$exists: bool} | {$in: [...]}}), rewrite({경로: 새 값}), delayMs, failStatus(gRPC 상태 이름), " +
                                               "times(N번 걸리면 끝), seconds(S초 뒤 끝). 경로는 protobuf JSON 이름(fault.methods). 걸릴 때마다 fault.hit, 끝나면 fault.ended")]
    public object Add(string method, string dir, string action, string? playerId = null, JsonElement? match = null, JsonElement? rewrite = null,
        int delayMs = 0, string? failStatus = null, int? times = null, double? seconds = null)
    {
        if (!Enum.TryParse<FaultDirection>(dir, ignoreCase: true, out var direction))
            throw new ArgumentException($"dir '{dir}': request 또는 response");
        if (!Enum.TryParse<FaultActionKind>(action, ignoreCase: true, out var actionKind))
            throw new ArgumentException($"action '{action}': rewrite, drop, delay, fail 중 하나");

        var matchMap = ToMap(match, nameof(match));
        var rewriteMap = ToMap(rewrite, nameof(rewrite));
        if (actionKind == FaultActionKind.Rewrite && rewriteMap.Count == 0) throw new ArgumentException("action=rewrite에는 rewrite({경로: 새 값})가 필요");
        if (actionKind == FaultActionKind.Delay && delayMs <= 0) throw new ArgumentException("action=delay에는 delayMs(>0)가 필요");
        if (times is <= 0) throw new ArgumentException("times는 1 이상");
        if (seconds is <= 0) throw new ArgumentException("seconds는 0보다 커야 함");

        var status = StatusCode.Unavailable;
        if (failStatus != null && !Enum.TryParse(failStatus, ignoreCase: true, out status))
            throw new ArgumentException($"failStatus '{failStatus}': gRPC 상태 이름(Unavailable, Internal, DeadlineExceeded ...)");

        // 메서드와 경로를 걸기 전에 확인한다 - 오타로 규칙이 조용히 아무 일도 안 하지 않게.
        if (method != "*")
        {
            var found = GrpcMethods().FirstOrDefault(m => m.Method.FullName == method);
            if (found.Method == null)
                throw new ArgumentException($"이 서버에 gRPC 메서드 '{method}'가 없음 - fault.methods로 볼 것");
            var descriptor = direction == FaultDirection.Request ? found.Request : found.Response;
            if (descriptor != null)
            {
                foreach (var path in matchMap.Keys.Concat(rewriteMap.Keys))
                {
                    if (FaultRuleSet.CheckPath(descriptor, path) is { } error) throw new ArgumentException(error);
                }
            }
        }

        var rule = _rules.Add(id => new FaultRule
        {
            Id = id,
            Method = method,
            Direction = direction,
            PlayerId = string.IsNullOrEmpty(playerId) ? null : playerId,
            Match = matchMap,
            Action = actionKind,
            Rewrite = rewriteMap,
            DelayMs = delayMs,
            FailStatus = status,
            Remaining = times,
            ExpiresAt = seconds is { } s ? DateTimeOffset.UtcNow.AddSeconds(s) : null,
        });
        return rule.View();
    }

    [HarnessCommand("fault.remove", Description = "장애 규칙을 끈다(ruleId). 끄면 fault.ended(reason=removed)")]
    public object Remove(string ruleId) =>
        _rules.Remove(ruleId) ? new { ruleId, removed = true } : throw new ArgumentException($"규칙 '{ruleId}' 없음(이미 끝났을 수 있음 - fault.list)");

    [HarnessCommand("fault.list", Description = "지금 켜진 장애 규칙(걸린 횟수, 남은 횟수, 끝나는 시각)")]
    public object List() => new { rules = _rules.List() };

    private static IReadOnlyDictionary<string, JsonElement> ToMap(JsonElement? value, string name)
    {
        if (value is not { } v || v.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined) return new Dictionary<string, JsonElement>();
        if (v.ValueKind != JsonValueKind.Object) throw new ArgumentException($"{name}는 {{경로: 값}} 객체여야 함");
        return v.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone(), StringComparer.Ordinal);
    }

    private IEnumerable<(IMethod Method, MessageDescriptor? Request, MessageDescriptor? Response)> GrpcMethods()
    {
        var source = _services.GetService<EndpointDataSource>();
        if (source == null) yield break;
        foreach (var endpoint in source.Endpoints)
        {
            if (endpoint.Metadata.GetMetadata<GrpcMethodMetadata>() is not { } metadata) continue;
            var args = metadata.Method.GetType().IsGenericType ? metadata.Method.GetType().GetGenericArguments() : Type.EmptyTypes;
            yield return (metadata.Method, args.Length == 2 ? DescriptorOf(args[0]) : null, args.Length == 2 ? DescriptorOf(args[1]) : null);
        }
    }

    private static MessageDescriptor? DescriptorOf(Type type) =>
        typeof(IMessage).IsAssignableFrom(type)
            ? type.GetProperty("Descriptor", BindingFlags.Public | BindingFlags.Static)?.GetValue(null) as MessageDescriptor
            : null;

    /// <summary>메시지 이름과 필드 경로(메시지 필드는 한 단계 더).</summary>
    private static object? Describe(MessageDescriptor? descriptor)
    {
        if (descriptor == null) return null;
        var paths = new List<string>();
        foreach (var field in descriptor.Fields.InDeclarationOrder())
        {
            paths.Add(field.JsonName + TypeSuffix(field));
            if (field.FieldType == FieldType.Message && !field.IsRepeated && !field.IsMap)
                paths.AddRange(field.MessageType.Fields.InDeclarationOrder().Select(f => $"{field.JsonName}.{f.JsonName}{TypeSuffix(f)}"));
        }
        return new { name = descriptor.Name, fields = paths };
    }

    private static string TypeSuffix(FieldDescriptor f) =>
        f.IsMap ? ":map" : f.IsRepeated ? ":list" : f.FieldType switch
        {
            FieldType.Message => ":object",
            FieldType.Enum => ":enum",
            FieldType.String => ":string",
            FieldType.Bool => ":bool",
            _ => ":number",
        };
}
