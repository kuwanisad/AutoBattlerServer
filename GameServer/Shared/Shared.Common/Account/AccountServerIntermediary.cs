using Grpc.Net.Client;

namespace Shared.Common.Account;

/// <summary>
/// AccountServer의 gRPC API 전체를 감싼 범용 클라이언트. 메일과 직접 엮이지 않는다 -
/// 나중에 아이템 지급/밴 처리/VIP 혜택 등으로 확장될 여지가 크기 때문(TODO.md 참고).
///
/// 서버별로 필요한 메서드가 생기면 상속으로 확장한다(단, 추가되는 메서드도
/// "AccountServer 호출"이라는 단일 책임을 벗어나면 안 됨 - 그 서버의 다른 무관한
/// 로직이 여기 얹히면 안 됨).
/// </summary>
public abstract class AccountServerIntermediary
{
    /// <summary>서브클래스가 자기 MasterConnectionService.AccountServerAddress로 채운다.</summary>
    protected abstract string AccountServerAddress { get; }

    public async Task<AccountServer.GrantMailResponse> GrantMailAsync(
        string playerId, string sourceEventId, GameServer.Common.MailType mailType,
        string displayContent, string payloadJson, long expiresAtUnixMs = 0)
    {
        if (string.IsNullOrEmpty(AccountServerAddress))
        {
            return new AccountServer.GrantMailResponse { Success = false, ErrorMessage = "계정 서버 주소 없음" };
        }

        using var channel = GrpcChannelFactory.Create(AccountServerAddress);
        var client = new AccountServer.AccountControl.AccountControlClient(channel);
        return await client.GrantMailAsync(new AccountServer.GrantMailRequest
        {
            PlayerId = playerId,
            SourceEventId = sourceEventId,
            MailType = mailType,
            DisplayContent = displayContent,
            PayloadJson = payloadJson,
            ExpiresAtUnixMs = expiresAtUnixMs,
        });
    }

    public async Task<AccountServer.GetMailListResponse> GetMailListAsync(string playerId)
    {
        if (string.IsNullOrEmpty(AccountServerAddress)) return new AccountServer.GetMailListResponse();

        using var channel = GrpcChannelFactory.Create(AccountServerAddress);
        var client = new AccountServer.AccountControl.AccountControlClient(channel);
        return await client.GetMailListAsync(new AccountServer.GetMailListRequest { PlayerId = playerId });
    }

    public async Task<AccountServer.OpenMailResponse> OpenMailAsync(string playerId, string mailId)
    {
        if (string.IsNullOrEmpty(AccountServerAddress))
        {
            return new AccountServer.OpenMailResponse { Success = false, ErrorMessage = "계정 서버 주소 없음" };
        }

        using var channel = GrpcChannelFactory.Create(AccountServerAddress);
        var client = new AccountServer.AccountControl.AccountControlClient(channel);
        return await client.OpenMailAsync(new AccountServer.OpenMailRequest { PlayerId = playerId, MailId = mailId });
    }

    public async Task<AccountServer.GetFriendsResponse> GetFriendsAsync(string playerId)
    {
        if (string.IsNullOrEmpty(AccountServerAddress)) return new AccountServer.GetFriendsResponse();

        using var channel = GrpcChannelFactory.Create(AccountServerAddress);
        var client = new AccountServer.AccountControl.AccountControlClient(channel);
        return await client.GetFriendsAsync(new AccountServer.GetFriendsRequest { PlayerId = playerId });
    }

    public async Task<AccountServer.AddFriendResponse> AddFriendAsync(string playerId, string friendPlayerId)
    {
        if (string.IsNullOrEmpty(AccountServerAddress))
        {
            return new AccountServer.AddFriendResponse { Success = false, ErrorMessage = "계정 서버 주소 없음" };
        }

        using var channel = GrpcChannelFactory.Create(AccountServerAddress);
        var client = new AccountServer.AccountControl.AccountControlClient(channel);
        return await client.AddFriendAsync(new AccountServer.AddFriendRequest
        {
            PlayerId = playerId,
            FriendPlayerId = friendPlayerId,
        });
    }

    public async Task<AccountServer.RemoveFriendResponse> RemoveFriendAsync(string playerId, string friendPlayerId)
    {
        if (string.IsNullOrEmpty(AccountServerAddress))
        {
            return new AccountServer.RemoveFriendResponse { Success = false, ErrorMessage = "계정 서버 주소 없음" };
        }

        using var channel = GrpcChannelFactory.Create(AccountServerAddress);
        var client = new AccountServer.AccountControl.AccountControlClient(channel);
        return await client.RemoveFriendAsync(new AccountServer.RemoveFriendRequest
        {
            PlayerId = playerId,
            FriendPlayerId = friendPlayerId,
        });
    }
}
