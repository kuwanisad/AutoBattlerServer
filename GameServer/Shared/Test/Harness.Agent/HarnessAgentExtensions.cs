using Harness.Shared;
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Harness.Agent;

public static class HarnessAgentExtensions
{
    /// <summary>하네스 주소(예: http://localhost:9000). 테스트 하네스가 서버를 띄울 때 넣어준다.</summary>
    public const string AddressVariable = HarnessSharedConstants.EnvironmentVariables.Address;

    /// <summary>하네스에서 이 프로세스를 부를 이름. 없으면 실행 파일(어셈블리) 이름.</summary>
    public const string NameVariable = HarnessSharedConstants.EnvironmentVariables.Name;

    /// <summary>
    /// 테스트 하네스 에이전트를 등록한다. 서버 코드가 부르지 않는다 - <see cref="HarnessHostingStartup"/>이 부른다.
    /// HARNESS_ADDRESS 환경 변수가 없으면 빈 IHarnessEvents만 등록하고 끝 - 접속도, 명령 등록도 하지 않는다.
    ///
    /// 명령은 이 프로젝트(공통 명령)와 서버별 명령 어셈블리(Shared/Test/&lt;서버&gt;.Harness - 하네스 폴더에 있을 때)에서
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

        // 서버 어셈블리 자체는 찾지 않는다 - 서버 코드에 하네스 명령을 두지 않는다(테스트 코드 분리).
        var assemblies = new List<Assembly> { typeof(HarnessAgentService).Assembly };
        if (HarnessLocation.LoadServerCommands(processKind) is { } serverCommands) assemblies.Add(serverCommands);
        assemblies.AddRange(extraCommandAssemblies);

        // 여기서 바로 만든다 - 명령 이름 중복 같은 실수가 서버 시작 시점에 바로 터지게.
        var registry = new HarnessCommandRegistry(assemblies);

        services.AddSingleton(new HarnessAgentOptions(address, name, processKind));
        services.AddSingleton(registry);
        services.AddSingleton<HarnessAgentService>();
        services.AddSingleton<IHarnessEvents>(sp => sp.GetRequiredService<HarnessAgentService>());
        services.AddHostedService(sp => sp.GetRequiredService<HarnessAgentService>());

        // 경계 장애 규칙(fault.*): gRPC 인터셉터를 서버 자신의 인터셉터(인증 등) 안쪽에 끼운다 - PostConfigure는 서버 코드의
        // AddGrpc(options => ...)보다 나중에 적용된다. 규칙이 없으면 메시지를 건드리지 않는다. gRPC를 안 쓰는 서버에는 영향 없음.
        services.AddSingleton<Fault.FaultRuleSet>();
        services.PostConfigure<Grpc.AspNetCore.Server.GrpcServiceOptions>(o => o.Interceptors.Add<Fault.FaultInterceptor>());
        return services;
    }
}
