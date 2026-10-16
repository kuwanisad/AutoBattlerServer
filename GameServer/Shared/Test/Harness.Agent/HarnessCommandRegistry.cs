using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Harness.Protocol;
using Microsoft.Extensions.DependencyInjection;

namespace Harness.Agent;

/// <summary>
/// [HarnessCommand]가 붙은 메서드를 모아두고, 하네스가 보낸 Command를 실제 호출로 바꾼다.
/// 인자 변환은 protobuf Struct → JSON → System.Text.Json 역직렬화로 한다 - 파라미터 타입마다
/// 변환 코드를 따로 쓰지 않으려고(int/string/bool/enum/배열/클래스 모두 같은 경로).
/// </summary>
public sealed class HarnessCommandRegistry
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly Dictionary<string, Entry> _commands = new(StringComparer.Ordinal);

    public HarnessCommandRegistry(IEnumerable<Assembly> assemblies)
    {
        const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic |
                                   BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly;

        foreach (var assembly in assemblies.Distinct())
        {
            foreach (var type in SafeGetTypes(assembly))
            {
                foreach (var method in type.GetMethods(flags))
                {
                    var attribute = method.GetCustomAttribute<HarnessCommandAttribute>();
                    if (attribute == null) continue;

                    if (_commands.TryGetValue(attribute.Name, out var existing))
                    {
                        throw new InvalidOperationException(
                            $"하네스 명령 이름 중복: '{attribute.Name}' " +
                            $"({existing.Method.DeclaringType?.FullName}.{existing.Method.Name} / {type.FullName}.{method.Name})");
                    }

                    _commands[attribute.Name] = new Entry(attribute, method);
                }
            }
        }
    }

    public IReadOnlyCollection<string> Names => _commands.Keys;

    /// <summary>Hello에 실을 명령 목록.</summary>
    public IEnumerable<CommandInfo> Describe()
    {
        foreach (var (name, entry) in _commands.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            var info = new CommandInfo { Name = name, Description = entry.Attribute.Description };
            foreach (var parameter in entry.Method.GetParameters())
            {
                if (parameter.ParameterType == typeof(CancellationToken)) continue;
                info.Parameters.Add(new Harness.Protocol.ParameterInfo
                {
                    Name = parameter.Name ?? "",
                    Type = FriendlyTypeName(parameter.ParameterType),
                    Optional = parameter.HasDefaultValue,
                });
            }
            yield return info;
        }
    }

    /// <summary>명령 하나를 실행한다. 어떤 실패든 예외 대신 ok=false 결과로 돌려준다.</summary>
    public async Task<CommandResult> InvokeAsync(Command command, IServiceProvider services, CancellationToken cancellationToken)
    {
        var result = new CommandResult { Id = command.Id };

        if (!_commands.TryGetValue(command.Name, out var entry))
        {
            result.Ok = false;
            result.Error = $"모르는 명령 '{command.Name}'";
            return result;
        }

        object?[] arguments;
        try
        {
            arguments = BindArguments(entry.Method, command.Args, cancellationToken);
        }
        catch (Exception ex)
        {
            result.Ok = false;
            result.Error = $"인자 오류: {ex.Message}";
            return result;
        }

        try
        {
            object? target = null;
            if (!entry.Method.IsStatic)
            {
                // 호출마다 새로 만든다 - 명령 클래스는 상태 없이, 필요한 서버 서비스만 생성자로 받는다.
                using var scope = services.CreateScope();
                target = ActivatorUtilities.CreateInstance(scope.ServiceProvider, entry.Method.DeclaringType!);
                var value = await UnwrapAsync(entry.Method.Invoke(target, arguments));
                SetValue(result, value);
            }
            else
            {
                var value = await UnwrapAsync(entry.Method.Invoke(null, arguments));
                SetValue(result, value);
            }

            result.Ok = true;
        }
        catch (Exception ex)
        {
            var inner = ex is TargetInvocationException { InnerException: { } e } ? e : ex;
            result.Ok = false;
            result.Error = $"{inner.GetType().Name}: {inner.Message}";
        }

        return result;
    }

    private static object?[] BindArguments(MethodInfo method, Struct? args, CancellationToken cancellationToken)
    {
        var fields = args?.Fields ?? new Google.Protobuf.Collections.MapField<string, Value>();
        var parameters = method.GetParameters();
        var arguments = new object?[parameters.Length];
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (var i = 0; i < parameters.Length; i++)
        {
            var parameter = parameters[i];
            if (parameter.ParameterType == typeof(CancellationToken))
            {
                arguments[i] = cancellationToken;
                continue;
            }

            var match = fields.FirstOrDefault(f => string.Equals(f.Key, parameter.Name, StringComparison.OrdinalIgnoreCase));
            if (match.Key == null)
            {
                if (!parameter.HasDefaultValue) throw new ArgumentException($"'{parameter.Name}' 누락");
                arguments[i] = parameter.DefaultValue;
                continue;
            }

            used.Add(match.Key);
            var json = JsonFormatter.Default.Format(match.Value);
            try
            {
                arguments[i] = JsonSerializer.Deserialize(json, parameter.ParameterType, JsonOptions);
            }
            catch (JsonException ex)
            {
                throw new ArgumentException(
                    $"'{parameter.Name}'을(를) {FriendlyTypeName(parameter.ParameterType)}로 바꿀 수 없음(값={json}): {ex.Message}");
            }
        }

        // 오타로 엉뚱한 인자를 보내면 조용히 기본값으로 실행되는 사고를 막는다.
        var unknown = fields.Keys.Where(k => !used.Contains(k)).ToArray();
        if (unknown.Length > 0)
        {
            throw new ArgumentException($"모르는 인자: {string.Join(", ", unknown)}");
        }

        return arguments;
    }

    private static async Task<object?> UnwrapAsync(object? returned)
    {
        if (returned is not Task task) return returned;

        await task;
        var taskType = task.GetType();
        // Task<T>면 Result를 꺼낸다(비제네릭 Task나 내부 VoidTaskResult는 값 없음).
        if (!taskType.IsGenericType) return null;
        var result = taskType.GetProperty("Result")?.GetValue(task);
        return result?.GetType().Name == "VoidTaskResult" ? null : result;
    }

    private static void SetValue(CommandResult result, object? value)
    {
        if (value == null) return;
        var json = JsonSerializer.Serialize(value, JsonOptions);
        result.Value = JsonParser.Default.Parse<Value>(json);
    }

    private static IEnumerable<System.Type> SafeGetTypes(Assembly assembly)
    {
        try { return assembly.GetTypes(); }
        catch (ReflectionTypeLoadException ex) { return ex.Types.Where(t => t != null)!; }
    }

    private static string FriendlyTypeName(System.Type type)
    {
        var underlying = Nullable.GetUnderlyingType(type);
        if (underlying != null) return FriendlyTypeName(underlying) + "?";

        return type == typeof(int) ? "int"
            : type == typeof(long) ? "long"
            : type == typeof(string) ? "string"
            : type == typeof(bool) ? "bool"
            : type == typeof(double) ? "double"
            : type.IsEnum ? $"enum({string.Join("|", System.Enum.GetNames(type))})"
            : type.Name;
    }

    private sealed record Entry(HarnessCommandAttribute Attribute, MethodInfo Method);
}
