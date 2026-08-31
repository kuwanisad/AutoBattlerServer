using Shared.Common.Redis;

namespace Shared.Common.Auth;

/// <summary>
/// 제출된 토큰이 세션의 현재/직전 토큰 중 하나와 일치하는지 확인한다. 클라를 받는 모든
/// 서버(로비의 EnterLobby, 나중에 게임 에이전트의 ConnectionApproval 등)가 이 판단
/// 기준을 공유해야 하므로 여기 하나로 모아둔다.
///
/// 2슬롯(CurrentToken/PreviousToken)을 두는 이유: 모바일 환경 특성상 네트워크가 자주
/// 끊겼다 이어지는데, 그 사이 서버 쪽에서 토큰이 회전(재발급)되면 클라가 아직 직전 세대
/// 토큰을 들고 있을 수 있다. 그 경우까지 봐주지 않으면 진짜 본인인데도 튕겨나가고,
/// 로그인 서버는 재접속 개념이 없어서(처음부터 다시 로그인해야 함) 손해가 크다 -
/// 그래서 "방금 밀려난 값 한 세대까지"는 허용한다.
///
/// 순수 함수라 Redis 접근/DI가 필요 없다 - 세션 조회(ISessionRegistry.GetAsync)는
/// 호출하는 쪽이 이미 하고 있는 걸 그대로 넘겨받아서 비교만 한다.
/// </summary>
public static class TokenVerification
{
    public static bool IsValid(PlayerSession? session, string submittedToken)
    {
        if (session == null) return false;
        if (string.IsNullOrEmpty(submittedToken)) return false;

        return submittedToken == session.CurrentToken || submittedToken == session.PreviousToken;
    }
}
