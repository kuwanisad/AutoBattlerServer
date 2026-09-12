using System.Collections.Concurrent;

namespace LobbyServer.Services;

/// <summary>
/// 클라가 로비에 요청해서 진행 중인 작업 종류. 지금은 매칭 하나뿐이지만, 나중에
/// "친구 초대 응답 대기" 같은 것도 여기 추가될 수 있게 열어둔다.
/// </summary>
public enum RequestedClientTask
{
    Matching,
}

/// <summary>
/// RequestedClientTask 하나의 진행 상태. StayConnected 연결이 끊겼을 때, 그 시점에
/// 각 작업이 "정리가 필요한 상태"인지 판단하는 데 쓴다.
/// </summary>
public enum RequestedClientTaskState
{
    /// <summary>요청은 보냈지만 아직 확정 응답 전(대기열 진입 등) - 연결이 끊기면
    /// 정리(취소 통보)가 필요하다.</summary>
    Pending,

    /// <summary>확정을 향해 이미 넘어간 상태(매치 확정 → 이동 중 등) - 이 시점부턴
    /// 소유권 자체가 다른 서버로 넘어가는 중이라, 이 로비가 나서서 취소할 대상이
    /// 아니다.</summary>
    Processing,

    /// <summary>정상적으로 끝남(성공/실패 상관없이) - 더 이상 추적할 필요 없음.
    /// 보통은 Tasks에서 바로 제거하지 이 값으로 남겨두지 않는다.</summary>
    Completed,
}

/// <summary>
/// 클라 하나(StayConnected 연결 하나)의 상태. LobbyConnectionRegistry가 playerId별로
/// 하나씩 들고 있다. 클라가 지금 요청해둔 작업들(RequestedClientTask)의 진행 상태를
/// 여기 모아둬서, StayConnected 연결이 끊겼을 때 "지금 정리해야 할 게 뭔지"를
/// 하드코딩된 가정 없이 실제 상태로 판단할 수 있게 한다 - 예전엔
/// HandleStayConnectedEndedAsync가 "매칭 중이었을 수도 있으니" 무조건 LeaveMatch를
/// 불렀는데(무해하지만 부정확함), 이제는 진짜 Matching 작업이 Pending일 때만 부른다.
/// </summary>
public sealed class ConnectedClientInfo
{
    public System.Threading.Channels.Channel<global::LobbyServer.StayConnectedEvent> Channel { get; }

    public ConcurrentDictionary<RequestedClientTask, RequestedClientTaskState> Tasks { get; } = new();

    public ConnectedClientInfo(System.Threading.Channels.Channel<global::LobbyServer.StayConnectedEvent> channel)
    {
        Channel = channel;
    }
}
