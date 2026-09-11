namespace MainServer.Common.MatchConnection;

/// <summary>
/// 매칭 서버가 MatchConnectionControl.Connect 스트림으로 push하는 MatchNotification을
/// 처리하는 인터페이스. 로비 서버만 이걸 구현해서 등록한다(매치 확정/취소를 대기 중인
/// RequestMatch 스트림으로 전달해야 하므로). 게임 에이전트/채팅 서버는 등록하지 않으며,
/// 그 경우 MatchServerConnection은 그냥 알림을 무시한다(애초에 매칭 서버가 로비가 아닌
/// 다른 타입에는 MatchNotification을 보내지 않는다).
/// </summary>
public interface IMatchNotificationHandler
{
    void HandleMatchNotification(global::MatchingServer.MatchNotification notification);

    /// <summary>매칭 서버와의 상시 연결이 끊겼을 때(재연결 시도 전) 호출된다. 그 순간
    /// 매칭 결과를 기다리고 있던 전원에게 실패를 알려야 한다 - 안 그러면 그 클라들의
    /// RequestMatch 스트림이 매칭 서버가 다시 살아날 때까지(또는 영원히) 응답 없이
    /// 멈춰있게 된다.</summary>
    void HandleConnectionLost();
}