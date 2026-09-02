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

    /// <summary>playerId로 친구 목록을 조회한다. 계정이 없으면 빈 목록.</summary>
    Task<IReadOnlyCollection<string>> GetFriendsAsync(string playerId);

    /// <summary>서로를 친구로 추가한다(대칭). 둘 중 하나라도 계정이 없으면 false.</summary>
    Task<bool> AddFriendAsync(string playerId, string friendPlayerId);

    /// <summary>서로를 친구 목록에서 제거한다(대칭). 원래 친구가 아니었어도 성공으로 본다
    /// (멱등 - 이미 없는 걸 지우는 건 에러가 아니라 목표 상태에 이미 도달한 것).</summary>
    Task<bool> RemoveFriendAsync(string playerId, string friendPlayerId);
}

/// <summary>계정 하나의 정보. 로그인 정보(ID/비번) 외에 나중에 다른 계정 속성이 늘어날 수 있어서
/// 별도 레코드 타입으로 분리해뒀다 - PlayerSession과는 다른 개념(그건 "지금 어디 접속해있는지",
/// 이건 "이 계정이 어떤 사람인지").</summary>
public sealed class AccountRecord
{
    public string AccountId { get; set; } = string.Empty;
    public string PasswordHash { get; set; } = string.Empty;
    public string PlayerId { get; set; } = string.Empty;
    public long CreatedAtUnixMs { get; set; }

    /// <summary>친구의 playerId 목록. HashSet 하나로 충분하다는 판단 - 친구 관계 자체에
    /// 별도 메타데이터(추가 시각 등)가 필요해지면 그때 확장.</summary>
    public HashSet<string> Friends { get; set; } = new();
}
