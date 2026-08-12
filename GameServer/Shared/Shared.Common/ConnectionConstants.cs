namespace Shared.Common;

/// <summary>
/// 서버 간 연결 유지에 관한 공통 상수.
/// 타임아웃과 인터벌은 서로 연관된 값이므로 한 곳에 모아 관계를 명확히 한다.
/// 타임아웃 = 인터벌 × 2~3배.
/// </summary>
public static class ConnectionConstants
{
    /// <summary>
    /// 업무 메시지가 없을 때 keepalive 용도로 보내는 주기.
    /// 이 시간 동안 보낼 메시지가 없으면 빈 하트비트를 보내 침묵을 방지한다.
    /// </summary>
    public static readonly TimeSpan KeepaliveInterval = TimeSpan.FromSeconds(5);

    /// <summary>
    /// 상대방으로부터 마지막 메시지 수신 후 이 시간이 지나면 죽은 것으로 간주한다.
    /// KeepaliveInterval의 2~3배로 잡아 일시적인 지연을 오판하지 않게 한다.
    /// </summary>
    public static readonly TimeSpan HeartbeatTimeout = TimeSpan.FromSeconds(15);

    /// <summary>연결 실패 시 재시도 대기 시간.</summary>
    public static readonly TimeSpan ReconnectDelay = TimeSpan.FromSeconds(5);
}
