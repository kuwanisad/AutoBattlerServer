using System.Threading.Channels;
using GameServer.Common;

namespace MasterServer.Registry;

/// <summary>마스터 서버에 등록된 메인 서버 인스턴스 하나의 상태.</summary>
public sealed class InstanceInfo
{
    public required string InstanceId { get; set; }
    public required ServerType ServerType { get; set; }
    public required string Address { get; set; }
    public int CurrentLoad { get; set; }
    public int Capacity { get; set; }

    /// <summary>
    /// 이 인스턴스로 내려보낼 메시지(하트비트, 명령)의 송신 큐.
    ///
    /// IServerStreamWriter.WriteAsync는 같은 스트림에 동시 호출이 안전하지 않은데,
    /// "주기적으로 도는 하트비트 타이머"와 "그때그때 들어오는 브로드캐스트 명령"처럼
    /// 서로 다른 두 출처가 같은 스트림에 동시에 쓰려고 할 수 있다. 락(세마포어)으로도
    /// 직렬화는 되지만, 그러면 호출자가 "지금 다른 쪽이 쓰는 중이니 기다려야 한다"는
    /// 사실을 직접 알아야 한다. 채널을 쓰면 양쪽 다 그냥 큐에 넣기만 하면 되고,
    /// 실제 WriteAsync 호출은 Connect 메서드 안의 단일 소비 루프 하나만 담당해서
    /// "한 스트림에 쓰는 주체는 항상 하나"라는 불변식이 코드 구조로 보장된다.
    /// </summary>
    public Channel<global::MasterServer.MasterMessage> OutboundQueue { get; } =
        Channel.CreateUnbounded<global::MasterServer.MasterMessage>(new UnboundedChannelOptions
        {
            SingleReader = true,  // Connect의 소비 루프 하나만 읽는다
            SingleWriter = false, // 하트비트 타이머와 명령 전송이 둘 다 쓸 수 있다
        });

    /// <summary>
    /// 이 항목을 등록한 Connect 호출(스트림)의 고유 식별자.
    /// 같은 instanceId로 재접속하면 새 토큰이 발급되며, 옛 Connect 호출이 뒤늦게
    /// 정리를 시도할 때 "지금 레지스트리에 있는 게 정말 내 연결인지"를 확인하는 데 쓴다.
    /// 이게 없으면, 부하로 정리가 지연된 옛 연결이 막 재접속한 새 연결을 잘못 지울 수 있다.
    /// </summary>
    public Guid ConnectionToken { get; set; }
}