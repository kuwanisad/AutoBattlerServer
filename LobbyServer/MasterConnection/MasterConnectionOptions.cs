namespace LobbyServer.MasterConnection;

/// <summary>마스터 서버 접속 관련 설정. appsettings.json의 "MasterConnection" 섹션에 매핑된다.</summary>
public sealed class MasterConnectionOptions
{
    public const string SectionName = "MasterConnection";

    /// <summary>마스터 서버 gRPC 엔드포인트 (예: http://localhost:5100).</summary>
    public string MasterAddress { get; set; } = "http://localhost:5100";

    /// <summary>
    /// 이 로비 인스턴스의 고유 식별자. 지금은 프로세스 시작 시 한 번 발급해 그 생애 동안 유지한다.
    /// 재시작 후에도 같은 ID를 유지해야 할 필요가 생기면(세션 레지스트리 연계 등) 설정 파일/환경변수로
    /// 고정값을 주입하는 식으로 바꿀 수 있다.
    /// </summary>
    public string InstanceId { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>
    /// 게임 클라이언트(또는 다른 메인 서버)가 이 로비 인스턴스에 접속할 주소.
    /// 지금은 자체 gRPC 서비스가 아직 없어서 자리만 잡아둔 값이다.
    /// </summary>
    public string SelfAddress { get; set; } = "localhost:5200";

    /// <summary>하트비트(상태 보고) 전송 주기. MasterHeartbeatTimeout의 1/2~1/3 이하여야 한다.</summary>
    public TimeSpan HeartbeatInterval { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// 마스터로부터 마지막 메시지 수신 후 이 시간이 지나면 죽은 것으로 간주하고 재연결을 시도한다.
    /// HeartbeatInterval의 2~3배로 잡아 일시적인 지연을 오판하지 않게 한다.
    /// 서버 쪽 ConnectionConstants.HeartbeatTimeout과 같은 기준이다.
    /// </summary>
    public TimeSpan MasterHeartbeatTimeout { get; set; } = TimeSpan.FromSeconds(15);

    /// <summary>접속이 끊겼을 때 재시도 간 최소/최대 대기 시간 (지수 백오프의 상하한).</summary>
    public TimeSpan ReconnectDelayMin { get; set; } = TimeSpan.FromSeconds(1);
    public TimeSpan ReconnectDelayMax { get; set; } = TimeSpan.FromSeconds(30);
}
