/// <summary>
/// 더미 채팅 필터.
/// 실제 필터링 없이 모든 메시지를 통과시킨다.
/// 추후 룰 기반 또는 ML 기반 구현체로 교체한다.
/// </summary>
public sealed class DummyChatFilter : IChatFilter
{
    public Task<FilterResult> FilterAsync(string senderId, string content)
    {
        // TODO: 욕설 필터링, 스팸 감지, 레이트 리밋 등 구현
        return Task.FromResult(FilterResult.Allow(content));
    }
}
