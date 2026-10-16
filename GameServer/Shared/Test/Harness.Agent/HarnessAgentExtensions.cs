using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Harness.Agent;

public static class HarnessAgentExtensions
{
    /// <summary>하네스 주소(예: http://localhost:9000). 테스트 하네스가 서버를 띄울 때 넣어준다.</summary>
    public const string AddressVariable = "HARNESS_ADDRESS";

    /// <summary>하네스에서 이 프로세스를 부를 이름. 없으면 실행 파일(어셈블리) 이름.</summary>
    public const string NameVariable = "HARNESS_NAME";

    /// <summary>
    /// 테스트 하네스 에이전트를 등록한다. HARNESS_ADDRESS 환경 변수가 없으면(평소 실행)
    /// 빈 IHarnessEvents만 등록하고 끝 - 접속도, 명령 등록도 하지 않는다.
    ///
    /// 명령은 이 프로젝트(공통 명령)와 실행 중인 서버 어셈블리(각 서버의 Test/Harness 폴더)에서
    /// 자동으로 찾는다. 다른 어셈블리에도 명령이 있으면 extraCommandAssemblies로 넘긴다.
    /// </summary>
    public static IServiceCollection AddHarnessAgent(
        this IServiceCollection services, params Assembly[] extraCommandAssemblies)
    {
        var address = Environment.GetEnvironmentVariable(AddressVariable);
        if (string.IsNullOrWhiteSpace(address))
        {
            services.TryAddSingleton<IHarnessEvents, NullHarnessEvents>();
            return services;
        }

        var entryAssembly = Assembly.GetEntryAssembly();
        var processKind = entryAssembly?.GetName().Name ?? "Unknown";
        var name = Environment.GetEnvironmentVariable(NameVariable);
        if (string.IsNullOrWhiteSpace(name)) name = processKind;

        var assemblies = new List<Assembly> { typeof(HarnessAgentService).Assembly };
        if (entryAssembly != null) assemblies.Add(entryAssembly);
        assemblies.AddRange(extraCommandAssemblies);

        // 여기서 바로 만든다 - 명령 이름 중복 같은 실수가 서버 시작 시점에 바로 터지게.
        var registry = new HarnessCommandRegistry(assemblies);

        services.AddSingleton(new HarnessAgentOptions(address, name, processKind));
        services.AddSingleton(registry);
        services.AddSingleton<HarnessAgentService>();
        services.AddSingleton<IHarnessEvents>(sp => sp.GetRequiredService<HarnessAgentService>());
        services.AddHostedService(sp => sp.GetRequiredService<HarnessAgentService>());
        return services;
    }
}
