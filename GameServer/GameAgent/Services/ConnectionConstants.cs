namespace GameAgent.Services;

/// <summary>
/// 게임 서버와의 연결 유지에 관한 상수.
/// 마스터 서버의 ConnectionConstants와 동일한 기준으로 설정한다.
/// </summary>
public static class ConnectionConstants
{
    /// <summary>
    /// 게임 에이전트가 게임 서버로 보내는 keepalive 주기.
    /// 이 시간 동안 보낼 업무 메시지가 없으면 빈 하트비트를 보낸다.
    /// </summary>
    public static readonly TimeSpan KeepaliveInterval = TimeSpan.FromSeconds(5);

    /// <summary>
    /// 게임 서버로부터 마지막 메시지 수신 후 이 시간이 지나면 죽은 것으로 간주한다.
    /// KeepaliveInterval의 2~3배로 잡는다.
    /// </summary>
    public static readonly TimeSpan HeartbeatTimeout = TimeSpan.FromSeconds(15);

    /// <summary>
    /// 게임 서버가 연결하기를 기다리는 최대 시간.
    /// 에디터에서 Play 버튼을 누를 시간을 고려해 넉넉하게 잡는다.
    /// </summary>
    public static readonly TimeSpan GameServerConnectTimeout = TimeSpan.FromSeconds(60);
}
