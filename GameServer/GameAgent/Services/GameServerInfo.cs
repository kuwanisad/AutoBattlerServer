using System.Threading.Channels;
using IngameServer;

namespace GameAgent.Services;

/// <summary>
/// 연결된 게임 서버의 상태와 송신 큐를 담는다.
/// MasterServer의 InstanceInfo와 동일한 패턴.
/// </summary>
public sealed class GameServerInfo
{
    /// <summary>게임 서버로 보낼 메시지 큐. SendLoopAsync가 단독 소비한다.</summary>
    public Channel<GameAgentMessage> OutboundQueue { get; } =
        Channel.CreateUnbounded<GameAgentMessage>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
        });

    /// <summary>게임 서버가 준비 완료를 알렸는지 여부.</summary>
    public bool IsReady { get; set; }

    /// <summary>현재 활성 매치 수.</summary>
    public int CurrentLoad { get; set; }

    /// <summary>수용 가능한 최대 매치 수.</summary>
    public int Capacity { get; set; }
}
