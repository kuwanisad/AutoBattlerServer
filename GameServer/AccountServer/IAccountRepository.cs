namespace AccountServer;

/// <summary>
/// 계정 정보 저장소. 지금은 인메모리 더미(AccountServer/Debug/InMemoryAccountRepository)만
/// 있고, 나중에 실제 SQL 기반 구현체로 교체될 자리 - 이 인터페이스를 쓰는 코드
/// (AccountControlService)는 구현체가 바뀌어도 안 바뀐다.
/// </summary>
public interface IAccountRepository
{
    /// <summary>계정 ID로 계정 정보를 조회한다. 없으면 null.</summary>
    Task<AccountRecord?> FindAsync(string accountId);

    /// <summary>새 계정을 만든다. 이미 있으면 false.</summary>
    Task<bool> TryCreateAsync(AccountRecord record);

    /// <summary>이미 발급된 playerId인지 확인한다 (GUID 충돌 방어용).</summary>
    Task<bool> PlayerIdExistsAsync(string playerId);
}

/// <summary>계정 하나의 정보. 로그인 정보(ID/비번) 외에 나중에 다른 계정 속성이 늘어날 수 있어서
/// 별도 레코드 타입으로 분리해뒀다 - PlayerSession과는 다른 개념(그건 "지금 어디 접속해있는지",
/// 이건 "이 계정이 어떤 사람인지").</summary>
public sealed class AccountRecord
{
    public string AccountId { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    public string PlayerId { get; set; } = string.Empty;
    public long CreatedAtUnixMs { get; set; }
}
