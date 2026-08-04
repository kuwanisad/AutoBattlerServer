/// <summary>
/// 채팅 메시지 필터링 추상화.
/// 기본 구현은 DummyChatFilter.
/// 추후 룰 기반 또는 ML 기반 구현체로 교체한다.
/// </summary>
public interface IChatFilter
{
    /// <summary>
    /// 메시지를 검사한다.
    /// 통과하면 true, 차단하면 false를 반환한다.
    /// </summary>
    Task<FilterResult> FilterAsync(string senderId, string content);
}

public sealed class FilterResult
{
    public bool Allowed { get; set; }
    public string? Reason { get; set; }           // 차단 사유
    public string? FilteredContent { get; set; }  // 필터링된 내용 (마스킹 처리 등)

    public static FilterResult Allow(string content) => new()
    {
        Allowed = true,
        FilteredContent = content,
    };

    public static FilterResult Block(string reason) => new()
    {
        Allowed = false,
        Reason = reason,
    };
}
