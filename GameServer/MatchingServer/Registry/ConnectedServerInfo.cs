using System.Threading.Channels;
using GameServer.Common;

namespace MatchingServer.Registry;

/// <summary>
/// 매칭 서버의 MatchConnectionControl.Connect 스트림에 접속한 로비/게임 에이전트 인스턴스 하나.
/// MasterServer.Registry.InstanceInfo와 같은 패턴이다.
/// </summary>
public sealed class ConnectedServerInfo
{
    public required string ServerId { get; set; }
    public required ServerType ServerType { get; set; }
    public required string Address { get; set; }

    /// <summary>이 인스턴스로 내려보낼 메시지(하트비트, MatchNotification)의 송신 큐.</summary>
    public Channel<global::MatchingServer.MatchToServerMessage> OutboundQueue { get; } =
        Channel.CreateUnbounded<global::MatchingServer.MatchToServerMessage>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
        });

    /// <summary>
    /// 이 항목을 등록한 Connect 호출(스트림)의 고유 식별자. 재접속 시 옛 연결이 새 연결을
    /// 실수로 지우는 것을 막는 데 쓴다 (MasterServer.Registry.InstanceInfo와 동일한 이유).
    /// </summary>
    public Guid ConnectionToken { get; set; }
}
