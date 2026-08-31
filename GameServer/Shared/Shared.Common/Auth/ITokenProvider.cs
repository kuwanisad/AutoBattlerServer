namespace Shared.Common.Auth;

/// <summary>
/// 토큰 발급/검증을 담당하는 인터페이스. 클라를 직접 받는 서버(로비의 EnterLobby, 나중에
/// 게임 에이전트의 ConnectionApproval 등)라면 전부 이걸 쓴다 - 각자 재구현하면 나중에
/// 토큰 정책(형식, 만료 등)이 바뀔 때 한쪽만 고치고 다른 쪽을 놓치는 사고가 날 수 있어서
/// 원래 LobbyServer 전용이던 걸 여기로 옮겼다.
///
/// 지금은 DummyTokenProvider(임시, 실질적 서명 검증 없음)만 있고, 나중에 실제 JWT
/// 라이브러리 기반 구현체로 교체될 자리 - 구현체가 바뀌어도 이 인터페이스를 쓰는 코드는
/// 안 바뀐다.
/// </summary>
public interface ITokenProvider
{
    /// <summary>주어진 playerId에 대한 새 토큰을 발급한다. 매번 다른 값이어야 한다 -
    /// 토큰이 playerId 등 예측 가능한 값과 같으면, 세션의 2슬롯(CurrentToken/PreviousToken)
    /// 대조가 실질적인 효과를 못 낸다(누구나 playerId를 알면 토큰도 아는 셈이 되므로).</summary>
    string GenerateToken(string playerId);

    /// <summary>토큰을 검증하고, 유효하면 playerId를 꺼낸다.</summary>
    bool ValidateToken(string token, out string playerId);
}
