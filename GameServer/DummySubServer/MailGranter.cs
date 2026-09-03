using Grpc.Net.Client;
using MainServer.Common.MasterConnection;
using Microsoft.Extensions.Logging;
using Shared.Common.Redis;

namespace DummySubServer;

/// <summary>
/// AccountServer.GrantMail 호출 + 소유 서버 알림(NotifyMailUpdated)을 한 번에 처리하는
/// 헬퍼. Kafka/RabbitMQ 컨슈머들이 공통으로 쓴다.
///
/// 순서(TODO.md 참고):
/// 1. AccountServer.GrantMail - 실제 지급(멱등, source_event_id로 중복 방지)
/// 2. 세션 레지스트리로 지금 이 플레이어를 누가 관리 중인지 조회
/// 3. 있으면(온라인이면) 그 서버 타입에 맞는 NotifyMailUpdated 호출(신호만, 내용 없음) -
///    없으면 아무것도 안 함(다음 접속 시 자동 제공이 안전망이라 실패로 취급 안 함).
/// </summary>
public sealed class MailGranter
{
    private readonly MasterConnectionService _masterConnection;
    private readonly ISessionRegistry _sessionRegistry;
    private readonly ILogger<MailGranter> _logger;

    public MailGranter(
        MasterConnectionService masterConnection,
        ISessionRegistry sessionRegistry,
        ILogger<MailGranter> logger)
    {
        _masterConnection = masterConnection;
        _sessionRegistry = sessionRegistry;
        _logger = logger;
    }

    public async Task GrantAndNotifyAsync(
        string playerId,
        string sourceEventId,
        string mailType,
        string displayContent,
        string payloadJson,
        long expiresAtUnixMs = 0)
    {
        if (string.IsNullOrEmpty(_masterConnection.AccountServerAddress))
        {
            _logger.LogWarning("계정 서버 주소 없음 - 우편 지급 불가: {PlayerId}", playerId);
            return;
        }

        AccountServer.GrantMailResponse grantResponse;
        try
        {
            using var accountChannel = CreateChannel(_masterConnection.AccountServerAddress);
            var accountClient = new AccountServer.AccountControl.AccountControlClient(accountChannel);

            grantResponse = await accountClient.GrantMailAsync(new AccountServer.GrantMailRequest
            {
                PlayerId = playerId,
                SourceEventId = sourceEventId,
                MailType = mailType,
                DisplayContent = displayContent,
                PayloadJson = payloadJson,
                ExpiresAtUnixMs = expiresAtUnixMs,
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "우편 지급 요청 실패: {PlayerId} MailType={MailType}", playerId, mailType);
            throw; // 호출부(컨슈머)가 이걸 보고 재큐잉 여부를 결정하게 - 여기서 삼키지 않음
        }

        if (!grantResponse.Success)
        {
            _logger.LogWarning("우편 지급 거부: {PlayerId} MailType={MailType} {Error}",
                playerId, mailType, grantResponse.ErrorMessage);
            return;
        }

        _logger.LogInformation("우편 지급 완료: {PlayerId} MailType={MailType} MailId={MailId}",
            playerId, mailType, grantResponse.MailId);

        await NotifyOwnerIfOnlineAsync(playerId);
    }

    private async Task NotifyOwnerIfOnlineAsync(string playerId)
    {
        OwnerInfo? owner;
        try
        {
            owner = await _sessionRegistry.GetOwnerAsync(playerId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "세션 레지스트리 조회 실패 - 알림 생략(다음 접속 시 자동 제공됨): {PlayerId}", playerId);
            return;
        }

        if (owner == null || !owner.IsAlive || string.IsNullOrEmpty(owner.Address))
        {
            _logger.LogInformation("플레이어 오프라인 - 알림 생략(다음 접속 시 자동 제공됨): {PlayerId}", playerId);
            return;
        }

        try
        {
            switch (owner.ServerType)
            {
                case GameServer.Common.ServerType.Lobby:
                    using (var channel = CreateChannel(owner.Address))
                    {
                        var client = new LobbyServer.LobbyControl.LobbyControlClient(channel);
                        var response = await client.NotifyMailUpdatedAsync(
                            new LobbyServer.NotifyMailUpdatedRequest { PlayerId = playerId });
                        _logger.LogInformation("로비에 우편함 갱신 신호 전달: {PlayerId} Success={Success}",
                            playerId, response.Success);
                    }
                    break;

                case GameServer.Common.ServerType.Ingame:
                    using (var channel = CreateChannel(owner.Address))
                    {
                        var client = new IngameServer.IngameControl.IngameControlClient(channel);
                        var response = await client.NotifyMailUpdatedAsync(
                            new IngameServer.NotifyMailUpdatedRequest { PlayerId = playerId });
                        _logger.LogInformation("게임 에이전트에 우편함 갱신 신호 전달: {PlayerId} Success={Success}",
                            playerId, response.Success);
                    }
                    break;

                default:
                    _logger.LogWarning("알 수 없는 소유 서버 타입 - 알림 생략: {PlayerId} Type={Type}",
                        playerId, owner.ServerType);
                    break;
            }
        }
        catch (Exception ex)
        {
            // 신호 전달 실패는 치명적이지 않다 - 다음 접속 시 자동 제공이 안전망이므로
            // 로그만 남기고 넘어간다(재시도 안 함).
            _logger.LogWarning(ex, "소유 서버에 우편함 갱신 신호 전달 실패(다음 접속 시 자동 제공됨): {PlayerId}", playerId);
        }
    }

    /// <summary>
    /// AccountServer/로비/게임 에이전트 호출 시 SocketsHttpHandler를 명시적으로 구성한다 -
    /// bare GrpcChannel.ForAddress로 호출했을 때 원인 불명의 "HTTP_1_1_REQUIRED" 오류가
    /// 재현된 적이 있어서(KNOWN_ISSUES.md 참고), 검증된 이 패턴을 그대로 재사용한다.
    /// </summary>
    private static GrpcChannel CreateChannel(string address)
    {
        var handler = new System.Net.Http.SocketsHttpHandler
        {
            EnableMultipleHttp2Connections = true,
            KeepAlivePingDelay = TimeSpan.FromSeconds(10),
            KeepAlivePingTimeout = TimeSpan.FromSeconds(5),
            KeepAlivePingPolicy = System.Net.Http.HttpKeepAlivePingPolicy.WithActiveRequests,
        };

        return GrpcChannel.ForAddress(address, new GrpcChannelOptions
        {
            HttpHandler = handler,
            DisposeHttpClient = true,
        });
    }
}
