using GameAgent.Services;
using Harness.Agent;

namespace GameAgent.Test.Harness;

/// <summary>
/// GameAgent 전용 하네스 명령(테스트 전용). 실제 코드는 건드리지 않고 이미 공개된 것만 읽는다.
/// </summary>
internal sealed class GameAgentHarnessCommands
{
    private readonly GameServerHandler _handler;

    public GameAgentHarnessCommands(GameServerHandler handler) => _handler = handler;

    /// <summary>
    /// 게임 서버(Unity) 연결 상태와 부하. TODO의 "CurrentLoad 누수" 후보를 확인하는 용도 -
    /// 게임이 정상 종료된 뒤에도 currentLoad가 0으로 안 돌아오면 재현된 것.
    /// </summary>
    [HarnessCommand("process.status", Description = "게임 서버 연결 여부, 준비 여부, 현재 부하/수용량")]
    public object Status()
    {
        var info = _handler.GetServerInfo();
        return new
        {
            gameServerConnected = info != null,
            isReady = info?.IsReady ?? false,
            currentLoad = info?.CurrentLoad ?? 0,
            capacity = info?.Capacity ?? 0,
        };
    }
}
