namespace ChatServer;

/// <summary>
/// 채팅방(1:1 친구 방) 영속 저장소. Redis 기반, 세션 레지스트리와 완전히 별개(chat:room:*,
/// chat:player:*:rooms 키스페이스).
///
/// 방 생성/멤버십 판단 자체는 로비 서버(또는 나중에 인게임 서버)가 친구 관계를 검증한
/// 뒤가 아니라, 채팅 서버가 RequestChat 처리 중 직접(읽기 전용으로 AccountServer.GetFriends를
/// 불러) 검증하고 여기 기록한다 - roomId가 두 playerId로 결정론적으로 정해지므로
/// "생성"은 사실상 "기록"에 가깝다.
/// </summary>
public interface IChatRoomStore
{
    /// <summary>두 playerId가 유효한 1:1 방의 멤버라고 기록한다. 멱등 - 이미 있으면 그대로.</summary>
    Task EnsureRoomAsync(string channelId, string playerIdA, string playerIdB);

    /// <summary>이 채널에서 playerId 본인이 아닌 다른 멤버(상대방)를 반환한다. 이 playerId가
    /// 멤버가 아니거나 방이 없으면 null.</summary>
    Task<string?> GetOtherMemberAsync(string channelId, string playerId);

    /// <summary>이 플레이어가 속한 모든 방 목록.</summary>
    Task<IReadOnlyList<ChatRoomRecord>> GetRoomsForPlayerAsync(string playerId);

    /// <summary>방을 완전히 삭제한다 (친구 삭제 시).</summary>
    Task RemoveRoomAsync(string channelId);

    /// <summary>두 playerId로 결정론적 channelId를 계산한다. 정렬해서 조합하므로 어느
    /// 순서로 넣어도 항상 같은 값이 나온다.</summary>
    static string ComputeChannelId(string playerIdA, string playerIdB)
    {
        var ordered = string.CompareOrdinal(playerIdA, playerIdB) <= 0
            ? (playerIdA, playerIdB)
            : (playerIdB, playerIdA);
        return $"friend:{ordered.Item1}:{ordered.Item2}";
    }
}

public sealed class ChatRoomRecord
{
    public string ChannelId { get; set; } = string.Empty;
    public string FriendPlayerId { get; set; } = string.Empty;
}
