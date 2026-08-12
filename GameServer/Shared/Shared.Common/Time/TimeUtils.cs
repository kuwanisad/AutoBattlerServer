namespace Shared.Common;

/// <summary>
/// 시간 관련 공통 유틸리티.
/// 시점 기록이 필요한 모든 곳에서 이 클래스를 사용한다.
/// 내부 구현이 바뀌어도 호출부를 수정할 필요가 없다.
/// </summary>
public static class TimeUtils
{
    /// <summary>현재 시각을 반환한다.</summary>
    public static long CurrentTimestamp() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
}
