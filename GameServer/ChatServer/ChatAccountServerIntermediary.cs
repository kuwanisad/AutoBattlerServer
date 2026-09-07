using MainServer.Common.MasterConnection;
using Shared.Common.Account;

namespace ChatServer;

/// <summary>채팅 서버 전용 AccountServerIntermediary. 지금은 공통 메서드(GetFriendsAsync -
/// RequestChat 검증용, 읽기 전용)만 쓰지만, 나중에 채팅 서버만 필요한 메서드(예: 채금
/// 요청)가 생기면 여기 추가한다.</summary>
public sealed class ChatAccountServerIntermediary : AccountServerIntermediary
{
    private readonly MasterConnectionService _masterConnection;

    public ChatAccountServerIntermediary(MasterConnectionService masterConnection)
    {
        _masterConnection = masterConnection;
    }

    protected override string AccountServerAddress => _masterConnection.AccountServerAddress;
}
