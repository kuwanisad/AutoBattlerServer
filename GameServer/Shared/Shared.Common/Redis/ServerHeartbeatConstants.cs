namespace Shared.Common.Redis;

/// <summary>
/// 서버 생존 TTL 관련 상수.
/// 모든 메인 서버가 이 값을 기준으로 하트비트를 보낸다.
/// </summary>
public static class ServerHeartbeatConstants
{
    /// <summary>서버가 Redis에 하트비트를 보내는 간격.</summary>
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(5);

    /// <summary>
    /// 서버 생존 TTL.
    /// 이 시간 안에 하트비트가 없으면 죽은 것으로 간주한다.
    /// Interval보다 충분히 길어야 일시적 네트워크 지연으로 오판하지 않는다.
    /// </summary>
    public static readonly TimeSpan Ttl = TimeSpan.FromSeconds(10);
}