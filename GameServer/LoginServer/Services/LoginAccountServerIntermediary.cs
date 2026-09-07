using MainServer.Common.MasterConnection;
using Shared.Common;
using Shared.Common.Account;

namespace LoginServer.Services;

/// <summary>
/// 로그인 서버 전용 AccountServerIntermediary. 계정 검증/생성(VerifyAccount/CreateAccount)은
/// 로그인 서버만 필요한 기능이라 여기 추가한다 - 기반 클래스(공용)엔 없음.
/// </summary>
public sealed class LoginAccountServerIntermediary : AccountServerIntermediary
{
    private readonly MasterConnectionService _masterConnection;

    public LoginAccountServerIntermediary(MasterConnectionService masterConnection)
    {
        _masterConnection = masterConnection;
    }

    protected override string AccountServerAddress => _masterConnection.AccountServerAddress;

    public async Task<AccountServer.VerifyAccountResponse> VerifyAccountAsync(
        string accountId, string password, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(AccountServerAddress))
        {
            return new AccountServer.VerifyAccountResponse { Found = false };
        }

        using var channel = GrpcChannelFactory.Create(AccountServerAddress);
        var client = new AccountServer.AccountControl.AccountControlClient(channel);
        return await client.VerifyAccountAsync(new AccountServer.VerifyAccountRequest
        {
            AccountId = accountId,
            Password = password,
        }, cancellationToken: cancellationToken);
    }

    public async Task<AccountServer.CreateAccountResponse> CreateAccountAsync(
        string accountId, string password, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(AccountServerAddress))
        {
            return new AccountServer.CreateAccountResponse
            {
                Success = false,
                ErrorMessage = "계정 서버 주소 없음",
            };
        }

        using var channel = GrpcChannelFactory.Create(AccountServerAddress);
        var client = new AccountServer.AccountControl.AccountControlClient(channel);
        return await client.CreateAccountAsync(new AccountServer.CreateAccountRequest
        {
            AccountId = accountId,
            Password = password,
        }, cancellationToken: cancellationToken);
    }
}