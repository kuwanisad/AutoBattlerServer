using Harness.Shared;
using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace Harness.Agent;

/// <summary>모든 서버에 공통으로 있는 명령.</summary>
internal sealed class BuiltInCommands
{
    private readonly HarnessAgentOptions _options;
    private readonly ILogger<BuiltInCommands> _logger;

    public BuiltInCommands(HarnessAgentOptions options, ILogger<BuiltInCommands> logger)
    {
        _options = options;
        _logger = logger;
    }

    [HarnessCommand(HarnessSharedConstants.Commands.ProcessInfo, Description = "프로세스 정보(이름, 종류, pid, 시작 시각, 가동 시간)")]
    public object Info()
    {
        var process = Process.GetCurrentProcess();
        var startedUtc = process.StartTime.ToUniversalTime();
        return new
        {
            name = _options.Name,
            kind = _options.ProcessKind,
            pid = Environment.ProcessId,
            startedAtUtc = startedUtc,
            uptimeSeconds = Math.Round((DateTime.UtcNow - startedUtc).TotalSeconds, 1),
        };
    }

    [HarnessCommand(HarnessSharedConstants.Commands.LogMark, Description = "서버 로그에 표시 한 줄을 남긴다 - 타임라인에서 시나리오 단계와 서버 로그를 맞춰 보기 위함")]
    public void Mark(string text)
    {
        _logger.LogInformation("===== 하네스 표시: {Text} =====", text);
    }
}
