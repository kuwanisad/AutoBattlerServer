namespace MainServer.Common.Ingame;

/// <summary>
/// 매치 종료 시 NGO 서버가 인게임 메인 서버에 전달하는 결과 데이터.
/// 인게임 메인 서버는 이를 Kafka에 발행하고, 보상 서버에 RabbitMQ로 전달한다.
/// </summary>
public sealed class MatchResult
{
    /// <summary>매치 고유 식별자.</summary>
    public string MatchId { get; init; } = string.Empty;

    /// <summary>매치에 참여한 플레이어 결과 목록.</summary>
    public IReadOnlyList<PlayerMatchResult> Players { get; init; } = [];

    /// <summary>매치 시작 시각 (UTC).</summary>
    public DateTimeOffset StartedAt { get; init; }

    /// <summary>매치 종료 시각 (UTC).</summary>
    public DateTimeOffset FinishedAt { get; init; }
}

public sealed class PlayerMatchResult
{
    public string PlayerId { get; init; } = string.Empty;
    public int Rank { get; init; }          // 1등, 2등, ...
    public bool IsWinner { get; init; }
    public int ScoreEarned { get; init; }   // 획득 점수
}
