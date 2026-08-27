namespace Shared.Common.Redis;

/// <summary>
/// 순수 남용 방지용 레이트 리밋. ISessionRegistry(관리 권한 기록부)와는 관심사가 달라서
/// 일부러 분리했다 - 권한 이전 여부랑 무관하게, 그냥 "이 키로 너무 자주 부르지 마라"만 본다.
///
/// 레이트 리밋 시간은 "실제 처리(예: 세션 반영)가 끝나는 시점"과 맞출 필요가 없다 - 둘은
/// 완전히 별개다. 레이트 리밋은 그냥 짧고 고정된 값으로 남용만 막고, 그 시간이 지나서
/// 다시 시도했을 때 실제로 처리가 아직 안 끝났으면 그건 실제 로직(예: EnterLobby의 소유권
/// 검증)이 정확하게 거부하면 될 일이다.
/// </summary>
public interface IRateLimiter
{
    /// <summary>
    /// key에 대해 이번 호출을 허용할지 원자적으로 판단한다. 허용되면 그 순간부터 window
    /// 동안 같은 key로는 다시 호출해도 거부된다.
    /// true = 허용(처리 진행), false = 거부(쿨다운 중이니 잠시 후 재시도 안내).
    /// </summary>
    Task<bool> TryAcquireAsync(string key, TimeSpan window);
}
