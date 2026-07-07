using Confluent.Kafka;
using MainServer.Common.MasterConnection;
using MainServer.Common.Messaging;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Text.Json;

namespace MainServer.Common.Ingame;

/// <summary>
/// 인게임 서버의 외부 통신을 담당한다.
/// - 마스터 서버와의 연결 유지 (MasterConnectionService)
/// - Kafka 발행 (매치 결과, 게임 이벤트)
/// - RabbitMQ 발행 (보상 지급 요청)
///
/// NGO 서버(Unity)와는 IIngameEventHandler 인터페이스로 통신한다.
/// NGO 서버가 게임 이벤트를 알리면, 이 클래스가 외부에 전달한다.
/// </summary>
public sealed class IngameMainServer : IIngameEventHandler
{
    private readonly MasterConnectionService _masterConnection;
    private readonly KafkaProducer<string, string> _kafkaProducer;
    private readonly RabbitMqProducer _rabbitMqProducer;
    private readonly ILogger<IngameMainServer> _logger;

    // 현재 접속 중인 플레이어 수 추적
    private int _connectedPlayers;
    private readonly int _capacity;

    public IngameMainServer(
        MasterConnectionService masterConnection,
        KafkaProducer<string, string> kafkaProducer,
        RabbitMqProducer rabbitMqProducer,
        ILogger<IngameMainServer> logger,
        int capacity = 8)   // TODO: 옵션으로 빼기
    {
        _masterConnection = masterConnection;
        _kafkaProducer = kafkaProducer;
        _rabbitMqProducer = rabbitMqProducer;
        _logger = logger;
        _capacity = capacity;
    }

    /// <summary>
    /// NGO 서버가 준비 완료됐을 때 호출한다.
    /// Capacity를 실제값으로 업데이트해서 마스터가 이 서버로 플레이어를 배정하기 시작하게 한다.
    /// </summary>
    public void NotifyReady()
    {
        _masterConnection.UpdateStatus(currentLoad: 0, capacity: _capacity);
        _logger.LogInformation("인게임 서버 개방 완료. Capacity={Capacity}", _capacity);
    }

    /// <summary>
    /// 서버 종료 시 호출한다.
    /// Capacity를 0으로 내려서 마스터가 신규 배정을 중단하게 한다.
    /// </summary>
    public void NotifyShutdown()
    {
        _masterConnection.UpdateStatus(currentLoad: _connectedPlayers, capacity: 0);
        _logger.LogInformation("인게임 서버 종료 신호 전송");
    }

    public void OnPlayerConnected(string playerId)
    {
        Interlocked.Increment(ref _connectedPlayers);
        _masterConnection.UpdateStatus(currentLoad: _connectedPlayers, capacity: _capacity);
        _logger.LogInformation("플레이어 접속: {PlayerId} ({Current}/{Capacity})",
            playerId, _connectedPlayers, _capacity);
    }

    public void OnPlayerDisconnected(string playerId)
    {
        Interlocked.Decrement(ref _connectedPlayers);
        _masterConnection.UpdateStatus(currentLoad: _connectedPlayers, capacity: _capacity);
        _logger.LogInformation("플레이어 접속 종료: {PlayerId} ({Current}/{Capacity})",
            playerId, _connectedPlayers, _capacity);
    }

    public async Task OnMatchFinishedAsync(MatchResult result)
    {
        _logger.LogInformation("매치 종료: {MatchId}", result.MatchId);

        // Kafka에 매치 결과 발행 (분석/감사 용도)
        var matchResultJson = JsonSerializer.Serialize(result);
        await _kafkaProducer.ProduceAsync("match-results", new Message<string, string>
        {
            Key = result.MatchId,
            Value = matchResultJson,
        });

        // RabbitMQ로 보상 지급 요청 (각 플레이어별)
        foreach (var player in result.Players)
        {
            var rewardRequest = JsonSerializer.Serialize(new
            {
                PlayerId = player.PlayerId,
                MatchId = result.MatchId,
                Rank = player.Rank,
                ScoreEarned = player.ScoreEarned,
            });
            _rabbitMqProducer.PublishToQueue("reward-queue", rewardRequest);
        }

        _logger.LogInformation("매치 결과 발행 완료: {MatchId} ({PlayerCount}명)",
            result.MatchId, result.Players.Count);
    }
}
