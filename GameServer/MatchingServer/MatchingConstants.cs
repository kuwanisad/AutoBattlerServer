using GameServer.Common;

namespace MatchingServer;

/// <summary>정원이 다 안 찼을 때 어떻게 할지 - 게임 모드마다 다를 수 있다.</summary>
public enum ShortfallPolicy
{
    /// <summary>정원이 찰 때까지 기다린다. MatchTimeout이 지나면 타임아웃으로 실패(기본값).</summary>
    RequireFull,
}

/// <summary>GameType 하나의 매칭 규칙.</summary>
/// <param name="RequiredPlayers">매치 하나에 모아야 하는 플레이어(사람) 수.</param>
/// <param name="Shortfall">정원 미달 시 처리.</param>
public sealed record MatchRule(int RequiredPlayers, ShortfallPolicy Shortfall = ShortfallPolicy.RequireFull);

/// <summary>
/// 매칭 서버가 쓰는 상수 모음 - Shared.Common.ConnectionConstants와 같은 방식으로 한 곳에 둔다.
/// 예전엔 타임아웃이 설정 키(MatchingServer:MatchTimeoutSeconds, 실제로 설정한 곳 없음)로,
/// 매칭 루프 간격이 Task.Delay(1000)으로 흩어져 있었다.
/// </summary>
public static class MatchingConstants
{
    /// <summary>대기열에 처음 들어온 시각부터 이 시간 안에 매치가 안 잡히면 타임아웃으로 실패.
    /// 매치가 중단돼 대기열로 돌아가도 처음 들어온 시각 기준으로 잰다(DisruptMatch).</summary>
    public static readonly TimeSpan MatchTimeout = TimeSpan.FromSeconds(30);

    /// <summary>매칭 루프(매치 시도 + 타임아웃 검사) 한 바퀴 간격.</summary>
    public static readonly TimeSpan MatchLoopInterval = TimeSpan.FromSeconds(1);

    /// <summary>
    /// GameType별 매칭 규칙. 여기 없는 GameType(Unspecified/None/Deleted 포함)은 대기열에 못 들어간다.
    /// </summary>
    private static readonly Dictionary<GameType, MatchRule> Rules = new()
    {
        // TODO: 테스트 동안만 1명. 원래 정원은 8(사람 1 + 봇 7) - 테스트가 끝나면 8로 되돌릴 것.
        [GameType.Normal] = new MatchRule(RequiredPlayers: 1),
    };

    public static bool TryGetRule(GameType gameType, out MatchRule rule) =>
        Rules.TryGetValue(gameType, out rule!);
}
