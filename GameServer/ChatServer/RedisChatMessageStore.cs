using System.Text.Json;
using StackExchange.Redis;

namespace ChatServer;

/// <summary>
/// Redis Sorted Set 기반 채팅 메시지 저장소. 점수(score) = 타임스탬프(ms)로 둬서
/// "이 시각 이전 N개" 같은 페이지네이션을 ZREVRANGEBYSCORE 하나로 처리한다.
///
/// 채널당 최대 개수를 넘으면 오래된 것부터 잘라낸다(예전 인메모리 구현의 500개 캡 계승).
/// </summary>
public sealed class RedisChatMessageStore : IChatMessageStore
{
    private const int MaxMessagesPerChannel = 500;

    private readonly ChatRedisConnection _connection;

    public RedisChatMessageStore(ChatRedisConnection connection)
    {
        _connection = connection;
    }

    private static string MessagesKey(string channelId) => $"chat:messages:{channelId}";

    public async Task SaveAsync(ChatMessageData message)
    {
        var db = await _connection.GetDatabaseAsync();
        var key = MessagesKey(message.ChannelId);
        var json = JsonSerializer.Serialize(message);

        await db.SortedSetAddAsync(key, json, message.TimestampUnixMs);

        var count = await db.SortedSetLengthAsync(key);
        if (count > MaxMessagesPerChannel)
        {
            // 랭크 0(가장 오래된 것)부터 초과분만큼 제거.
            await db.SortedSetRemoveRangeByRankAsync(key, 0, count - MaxMessagesPerChannel - 1);
        }
    }

    public async Task<IReadOnlyList<ChatMessageData>> GetRecentAsync(string channelId, int count)
    {
        var db = await _connection.GetDatabaseAsync();
        var entries = await db.SortedSetRangeByScoreAsync(
            MessagesKey(channelId), order: Order.Descending, take: count);
        return Parse(entries);
    }

    public async Task<IReadOnlyList<ChatMessageData>> GetBeforeAsync(
        string channelId, long beforeTimestampMs, int count)
    {
        var db = await _connection.GetDatabaseAsync();
        var entries = await db.SortedSetRangeByScoreAsync(
            MessagesKey(channelId),
            start: double.NegativeInfinity,
            stop: beforeTimestampMs,
            exclude: Exclude.Stop, // beforeTimestampMs 그 자체는 제외 - 엄밀히 "이전"
            order: Order.Descending,
            take: count);
        return Parse(entries);
    }

    private static IReadOnlyList<ChatMessageData> Parse(RedisValue[] entries)
    {
        var result = new List<ChatMessageData>(entries.Length);
        foreach (var entry in entries)
        {
            var data = JsonSerializer.Deserialize<ChatMessageData>(entry.ToString());
            if (data != null) result.Add(data);
        }
        return result;
    }
}
