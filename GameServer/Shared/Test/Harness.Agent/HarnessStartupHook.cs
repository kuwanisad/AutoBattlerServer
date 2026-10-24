using System.Reflection;
using System.Runtime.Loader;

/// <summary>
/// .NET 시작 훅 - DevTools가 하네스 모드로 서버를 띄울 때 환경 변수 DOTNET_STARTUP_HOOKS에 이 DLL 경로를 넣는다.
/// 그러면 .NET 런타임이 서버의 Main보다 먼저 <see cref="Initialize"/>를 부른다(이름 규칙: 네임스페이스 없는
/// StartupHook 클래스의 static Initialize - 런타임이 정한 것).
///
/// 하는 일은 하나: 서버가 참조하지 않는 어셈블리(이 DLL과 같은 폴더의 Harness.Agent, &lt;서버&gt;.Harness, 그 의존성)를
/// 이름으로 찾을 수 있게 한다. 서버 프로젝트는 하네스를 참조하지 않으므로, 이게 없으면 ASP.NET Core가
/// ASPNETCORE_HOSTINGSTARTUPASSEMBLIES의 "Harness.Agent"를 찾지 못하고 조용히 건너뛴다.
/// 서버가 이미 가진 어셈블리(같은 이름)는 서버 쪽이 먼저 쓰인다 - 이 처리기는 못 찾았을 때만 불린다.
///
/// 실제 등록은 <see cref="Harness.Agent.HarnessHostingStartup"/>이 한다.
/// </summary>
internal static class StartupHook
{
    public static void Initialize()
    {
        var directory = Path.GetDirectoryName(typeof(StartupHook).Assembly.Location);
        if (string.IsNullOrEmpty(directory)) return;

        AssemblyLoadContext.Default.Resolving += (context, name) =>
        {
            var path = Path.Combine(directory, name.Name + ".dll");
            return File.Exists(path) ? context.LoadFromAssemblyPath(path) : null;
        };
    }
}

namespace Harness.Agent
{
    /// <summary>하네스 폴더 위치(시작 훅으로 읽힌 이 DLL이 있는 폴더).</summary>
    internal static class HarnessLocation
    {
        public static string? Directory => Path.GetDirectoryName(typeof(HarnessLocation).Assembly.Location);

        /// <summary>
        /// 서버별 하네스 명령 어셈블리 - 이름 규칙 "&lt;서버 어셈블리 이름&gt;.Harness"(예: GameAgent.Harness), 하네스 폴더에
        /// 있으면 읽는다. 없는 서버는 공통 명령만 갖는다.
        /// </summary>
        public static Assembly? LoadServerCommands(string processKind)
        {
            var directory = Directory;
            if (string.IsNullOrEmpty(directory)) return null;

            var name = processKind + ".Harness";
            return File.Exists(Path.Combine(directory, name + ".dll")) ? Assembly.Load(new AssemblyName(name)) : null;
        }
    }
}
