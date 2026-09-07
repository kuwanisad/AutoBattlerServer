using MainServer.Common.MasterConnection;
using Shared.Common.Account;

namespace DummySubServer;

/// <summary>하위 서버 전용 AccountServerIntermediary. 지금은 공통 메서드만 쓰지만,
/// 나중에 하위 서버만 필요한 AccountServer 관련 메서드가 생기면 여기 추가한다.</summary>
public sealed class SubServerAccountServerIntermediary : AccountServerIntermediary
{
    private readonly MasterConnectionService _masterConnection;

    public SubServerAccountServerIntermediary(MasterConnectionService masterConnection)
    {
        _masterConnection = masterConnection;
    }

    protected override string AccountServerAddress => _masterConnection.AccountServerAddress;
}
