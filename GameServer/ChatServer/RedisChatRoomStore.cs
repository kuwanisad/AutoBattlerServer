using StackExchange.Redis;

namespace ChatServer;

/// <summary>
/// IChatRoomStore의 Redis 구현체. ChatRedisConnection(세션 레지스트리와 완전히 별개인
/// 채팅 서버 전용 연결)을 쓴다.
///
/// 키 구조:
///   chat:room:{channelId}:members   → Set&lt;playerId&gt; (정확히 2명)
///   chat:player:{playerId}:rooms    → Set&lt;channelId&gt; (이 플레이어가 속한 모든 방)
/// </summary>
public sealed class RedisChatRoomStore : IChatRoomStore
{
    private readonly ChatRedisConnection _connection;

    public RedisChatRoomStore(ChatRedisConnection connection)
    {
        _connection = connection;
    }

    private static string RoomMembersKey(string channelId) => $"chat:room:{channelId}:members";
    private static string PlayerRoomsKey(string playerId) => $"chat:player:{playerId}:rooms";

    public async Task EnsureRoomAsync(string channelId, string playerIdA, string playerIdB)
    {
        var db = await _connection.GetDatabaseAsync();

        var batch = db.CreateBatch();
        var t1 = batch.SetAddAsync(RoomMembersKey(channelId), playerIdA);
        var t2 = batch.SetAddAsync(RoomMembersKey(channelId), playerIdB);
        var t3 = batch.SetAddAsync(PlayerRoomsKey(playerIdA), channelId);
        var t4 = batch.SetAddAsync(PlayerRoomsKey(playerIdB), channelId);
        batch.Execute();

        await Task.WhenAll(t1, t2, t3, t4);
    }

    public async Task<string?> GetOtherMemberAsync(string channelId, string playerId)
    {
        var db = await _connection.GetDatabaseAsync();
        var members = await db.SetMembersAsync(RoomMembersKey(channelId));

        var memberIds = Array.ConvertAll(members, m => m.ToString());
        if (Array.IndexOf(memberIds, playerId) < 0) return null; // 이 playerId는 멤버가 아님

        foreach (var member in memberIds)
        {
            if (member != playerId) return member;
        }

        return null; // 상대방이 아직 없음(비정상 상태) - 방어적으로 null
    }

    public async Task<IReadOnlyList<ChatRoomRecord>> GetRoomsForPlayerAsync(string playerId)
    {
        var db = await _connection.GetDatabaseAsync();
        var channelIds = await db.SetMembersAsync(PlayerRoomsKey(playerId));

        var result = new List<ChatRoomRecord>();
        foreach (var channelIdValue in channelIds)
        {
            var channelId = channelIdValue.ToString();
            var other = await GetOtherMemberAsync(channelId, playerId);
            if (other != null)
            {
                result.Add(new ChatRoomRecord { ChannelId = channelId, FriendPlayerId = other });
            }
        }

        return result;
    }

    public async Task RemoveRoomAsync(string channelId)
    {
        var db = await _connection.GetDatabaseAsync();

        var members = await db.SetMembersAsync(RoomMembersKey(channelId));

        var batch = db.CreateBatch();
        var tasks = new List<Task> { batch.KeyDeleteAsync(RoomMembersKey(channelId)) };
        foreach (var member in members)
        {
            tasks.Add(batch.SetRemoveAsync(PlayerRoomsKey(member.ToString()), channelId));
        }
        batch.Execute();

        await Task.WhenAll(tasks);
    }
}
