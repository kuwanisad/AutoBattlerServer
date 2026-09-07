using MainServer.Common.MasterConnection;
using Shared.Common.Account;

namespace GameAgent.Services;

/// <summary>게임 에이전트 전용 AccountServerIntermediary. 지금은 공통 메서드만 쓰지만,
/// 게임 에이전트만 필요한 AccountServer 관련 메서드가 생기면 여기 추가한다.</summary>
public sealed class IngameAccountServerIntermediary : AccountServerIntermediary
{
    private readonly MasterConnectionService _masterConnection;

    public IngameAccountServerIntermediary(MasterConnectionService masterConnection)
    {
        _masterConnection = masterConnection;
    }

    protected override string AccountServerAddress => _masterConnection.AccountServerAddress;
}
