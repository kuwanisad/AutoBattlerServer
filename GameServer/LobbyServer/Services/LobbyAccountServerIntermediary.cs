using MainServer.Common.MasterConnection;
using Shared.Common.Account;

namespace LobbyServer.Services;

/// <summary>로비 전용 AccountServerIntermediary. 지금은 공통 메서드만 쓰지만, 로비만
/// 필요한 AccountServer 관련 메서드가 생기면 여기 추가한다(단, "AccountServer 호출"
/// 이라는 단일 책임을 벗어나면 안 됨).</summary>
public sealed class LobbyAccountServerIntermediary : AccountServerIntermediary
{
    private readonly MasterConnectionService _masterConnection;

    public LobbyAccountServerIntermediary(MasterConnectionService masterConnection)
    {
        _masterConnection = masterConnection;
    }

    protected override string AccountServerAddress => _masterConnection.AccountServerAddress;
}
