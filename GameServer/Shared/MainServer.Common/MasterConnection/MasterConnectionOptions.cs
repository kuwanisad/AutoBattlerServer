using MasterServer;
using GameServer.Common;

namespace MainServer.Common.MasterConnection;

/// <summary>마스터 서버 접속 관련 설정. appsettings.json의 "MasterConnection" 섹션에 매핑된다.</summary>
public sealed class MasterConnectionOptions
{
    public const string SectionName = "MasterConnection";

    /// <summary>마스터 서버 gRPC 엔드포인트 (예: http://localhost:5100).</summary>
    public string MasterAddress { get; set; } = "http://localhost:5100";

    /// <summary>이 메인 서버 인스턴스의 고유 식별자.</summary>
    public string InstanceId { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>게임 클라이언트(또는 다른 서버)가 이 인스턴스에 접속할 주소.</summary>
    public string SelfAddress { get; set; } = "localhost:5200";

    /// <summary>
    /// 이 인스턴스의 서버 타입. 로비 서버면 Lobby, 인게임 서버면 Ingame.
    /// 하드코딩하지 않고 옵션으로 빼서 MainServer.Common을 로비/인게임 모두 재사용한다.
    /// </summary>
    public ServerType ServerType { get; set; } = ServerType.Lobby;

    /// <summary>하트비트(상태 보고) 전송 주기.</summary>
    public TimeSpan HeartbeatInterval { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// 마스터로부터 마지막 메시지 수신 후 이 시간이 지나면 죽은 것으로 간주하고 재연결을 시도한다.
    /// HeartbeatInterval의 2~3배로 잡아 일시적인 지연을 오판하지 않게 한다.
    /// </summary>
    public TimeSpan MasterHeartbeatTimeout { get; set; } = TimeSpan.FromSeconds(15);

    /// <summary>접속이 끊겼을 때 재시도 간 최소/최대 대기 시간 (지수 백오프의 상하한).</summary>
    public TimeSpan ReconnectDelayMin { get; set; } = TimeSpan.FromSeconds(1);
    public TimeSpan ReconnectDelayMax { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>gRPC HTTP/2 KeepAlive ping 전송 주기.</summary>
    public TimeSpan KeepAlivePingDelay { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>gRPC HTTP/2 KeepAlive ping 응답 타임아웃.</summary>
    public TimeSpan KeepAlivePingTimeout { get; set; } = TimeSpan.FromSeconds(5);
}
