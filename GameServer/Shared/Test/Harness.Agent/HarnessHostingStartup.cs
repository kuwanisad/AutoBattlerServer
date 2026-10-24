using Microsoft.AspNetCore.Hosting;

[assembly: HostingStartup(typeof(Harness.Agent.HarnessHostingStartup))]

namespace Harness.Agent;

/// <summary>
/// ASP.NET Core 호스팅 시작 - 서버 코드에 하네스 흔적을 남기지 않고 하네스 에이전트를 등록하는 자리.
///
/// 언제, 누가 부르나: 서버의 WebApplication.CreateBuilder(args) 안에서 ASP.NET Core(GenericWebHostBuilder)가
/// 환경 변수 ASPNETCORE_HOSTINGSTARTUPASSEMBLIES에 적힌 어셈블리(Harness.Agent)를 읽어 이 클래스의 Configure를 부른다.
/// 서버 코드의 builder.Services... 줄보다 먼저 실행된다. 이 어셈블리를 찾는 건 <see cref="StartupHook"/>가 맡는다.
///
/// 두 환경 변수(DOTNET_STARTUP_HOOKS, ASPNETCORE_HOSTINGSTARTUPASSEMBLIES)는 DevTools(server-common.ps1)가 하네스 모드일
/// 때만 넣는다 - 평소 실행과 정식 빌드에는 하네스가 아예 없다(TestHarness/CLAUDE.md "테스트 코드 분리").
/// </summary>
public sealed class HarnessHostingStartup : IHostingStartup
{
    public void Configure(IWebHostBuilder builder)
    {
        builder.ConfigureServices(services => services.AddHarnessAgent());
    }
}
