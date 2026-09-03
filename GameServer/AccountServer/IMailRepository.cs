namespace AccountServer;

/// <summary>
/// 우편함 영속 저장소. 지금은 인메모리 더미(Debug/InMemoryMailRepository) - 진짜
/// SQL(JSON 컬럼 채택 - payload_json은 그대로 문자열 컬럼에 저장, mail_type/claimed
/// 등 자주 필터링할 값은 일반 컬럼)로 교체될 자리.
/// </summary>
public interface IMailRepository
{
    /// <summary>같은 원본 이벤트로 이미 지급된 적 있으면 그 mailId를 반환한다(멱등성).</summary>
    Task<string?> FindExistingBySourceEventIdAsync(string playerId, string sourceEventId);

    /// <summary>새 우편을 만든다. 반환값은 새로 발급된 mailId.</summary>
    Task<string> CreateAsync(MailRecord record);

    /// <summary>이 플레이어의 우편 전체(수령 여부 무관)를 반환한다.</summary>
    Task<IReadOnlyList<MailRecord>> GetForPlayerAsync(string playerId);

    Task<MailRecord?> FindAsync(string playerId, string mailId);

    /// <summary>
    /// 원자적으로 수령 처리한다. 이미 수령됐거나 만료됐거나 존재하지 않으면 실패
    /// (호출부가 정확한 사유를 구분해서 응답할 수 있게, 실패 이유는 별도로 조회).
    /// </summary>
    Task<bool> TryClaimAsync(string playerId, string mailId);
}

/// <summary>우편 하나의 데이터.</summary>
public sealed class MailRecord
{
    public string MailId { get; set; } = string.Empty;
    public string PlayerId { get; set; } = string.Empty;
    public string SourceEventId { get; set; } = string.Empty;
    public string MailType { get; set; } = string.Empty;
    public string DisplayContent { get; set; } = string.Empty;
    public string PayloadJson { get; set; } = string.Empty;
    public long CreatedAtUnixMs { get; set; }
    public long ExpiresAtUnixMs { get; set; } // 0이면 만료 없음
    public bool Claimed { get; set; }
}
