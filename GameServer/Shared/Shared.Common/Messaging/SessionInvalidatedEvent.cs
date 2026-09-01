namespace Shared.Common.Messaging;

/// <summary>
/// 세션이 삭제되는(로그아웃, 재접속 유예 시간 초과, 세션 충돌로 밀려남 등) 모든 지점에서
/// 발행하는 이벤트. "이 클라를 계속 서비스해도 되는지"는 전적으로 메인 서버(로비/게임
/// 에이전트)의 판단이고, 채팅 서버는 그 판단을 그대로 따라야 한다("채팅 서버는 서비스
/// 제공 가능 여부를 전적으로 메인 서버한테 맡긴다") - 채팅 서버가 이 이벤트를 구독해서
/// 해당 playerId의 웹소켓 연결을 강제 종료하는 데 쓴다.
///
/// 발행 책임은 세션 레지스트리 자체가 아니라, 세션을 지우기로 "결정"한 각 호출부가
/// 진다 - 세션 레지스트리는 순수 저장 역할로 남기고, "왜 지워지는지"는 그걸 결정한
/// 쪽만 아는 정보이기 때문.
/// </summary>
public sealed class SessionInvalidatedEvent
{
    public const string QueueName = "session-invalidated";

    public string PlayerId { get; set; } = string.Empty;
}
