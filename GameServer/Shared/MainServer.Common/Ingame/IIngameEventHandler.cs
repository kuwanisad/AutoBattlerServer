namespace MainServer.Common.Ingame;

/// <summary>
/// NGO 서버(Unity)와 인게임 메인 서버 사이의 인터페이스.
/// NGO 서버는 게임 이벤트가 발생하면 이 인터페이스를 통해 인게임 메인 서버에 알린다.
/// 인게임 메인 서버는 이 인터페이스를 구현해서 Kafka/RabbitMQ 발행, 마스터 상태 보고 등을 처리한다.
///
/// 같은 프로세스 안에서 C# 메서드 호출로 통신하므로 proto/gRPC가 필요 없다.
/// </summary>
public interface IIngameEventHandler
{
    /// <summary>
    /// 매치가 종료됐을 때 호출된다.
    /// 인게임 메인 서버는 Kafka에 매치 결과를 발행하고, 마스터에 부하를 업데이트한다.
    /// </summary>
    Task OnMatchFinishedAsync(MatchResult result);

    /// <summary>
    /// 플레이어가 접속했을 때 호출된다.
    /// 인게임 메인 서버는 마스터에 현재 부하를 업데이트한다.
    /// </summary>
    void OnPlayerConnected(string playerId);

    /// <summary>
    /// 플레이어가 접속을 끊었을 때 호출된다.
    /// 인게임 메인 서버는 마스터에 현재 부하를 업데이트한다.
    /// </summary>
    void OnPlayerDisconnected(string playerId);
}
