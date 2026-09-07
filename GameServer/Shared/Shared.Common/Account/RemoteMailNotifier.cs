using Grpc.Net.Client;
using Microsoft.Extensions.Logging;
using Shared.Common;
using Shared.Common.Redis;

namespace Shared.Common.Account;

/// <summary>
/// "지금 나랑 직접 붙어있는 클라"가 아니라 "제3자"(다른 곳에 있거나 오프라인일 수 있는
/// 플레이어)한테 우편을 보낼 때 쓴다 - 하위 서버(DummySubServer)뿐 아니라 메인 서버도
/// (경매장 낙찰금 지급처럼, 판매자가 지금 이 서버에 없을 수 있는 경우) 이 클래스를 쓴다.
///
/// AccountServerIntermediary를 주입받아서 쓸 뿐, 그 반대(AccountServerIntermediary가
/// 이 클래스를 아는 것)는 없다 - 의존 방향은 항상 이쪽에서 저쪽으로만.
/// </summary>
public sealed class RemoteMailNotifier
{
    private readonly AccountServerIntermediary _accountServer;
    private readonly ISessionRegistry _sessionRegistry;
    private readonly ILogger<RemoteMailNotifier> _logger;

    public RemoteMailNotifier(
        AccountServerIntermediary accountServer,
        ISessionRegistry sessionRegistry,
        ILogger<RemoteMailNotifier> logger)
    {
        _accountServer = accountServer;
        _sessionRegistry = sessionRegistry;
        _logger = logger;
    }

    public async Task GrantAndNotifyRemoteAsync(
        string playerId,
        string sourceEventId,
        string mailType,
        string displayContent,
        string payloadJson,
        long expiresAtUnixMs = 0)
    {
        var grantResponse = await _accountServer.GrantMailAsync(
            playerId, sourceEventId, mailType, displayContent, payloadJson, expiresAtUnixMs);

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

        if (owner == null)
        {
            _logger.LogInformation("세션 자체가 없음 - 알림 생략(다음 접속 시 자동 제공됨): {PlayerId}", playerId);
            return;
        }

        if (!owner.IsAlive || string.IsNullOrEmpty(owner.Address))
        {
            // IsAlive=false는 "그 플레이어가 접속 안 함"이 아니라 "소유 서버(OwnerServerId)
            // 자체의 생존 heartbeat(server:{serverId})가 없다"는 뜻이다 - 서버가 죽었거나,
            // 세션의 OwnerServerId가 이미 사라진 예전 인스턴스를 가리키고 있을 때 발생.
            _logger.LogInformation(
                "소유 서버가 살아있지 않음 - 알림 생략(다음 접속 시 자동 제공됨): {PlayerId} " +
                "OwnerServerId={OwnerServerId} OwnerServerType={OwnerServerType} IsAlive={IsAlive} Address={Address}",
                playerId, owner.ServerId, owner.ServerType, owner.IsAlive, owner.Address);
            return;
        }

        try
        {
            switch (owner.ServerType)
            {
                case GameServer.Common.ServerType.Lobby:
                    using (var channel = GrpcChannelFactory.Create(owner.Address))
                    {
                        var client = new LobbyServer.LobbyControl.LobbyControlClient(channel);
                        var response = await client.NotifyMailUpdatedAsync(
                            new LobbyServer.NotifyMailUpdatedRequest { PlayerId = playerId });
                        _logger.LogInformation("로비에 우편함 갱신 신호 전달: {PlayerId} Success={Success}",
                            playerId, response.Success);
                    }
                    break;

                case GameServer.Common.ServerType.Ingame:
                    using (var channel = GrpcChannelFactory.Create(owner.Address))
                    {
                        var client = new IngameServer.IngameControl.IngameControlClient(channel);
                        var response = await client.NotifyMailUpdatedAsync(
                            new IngameServer.NotifyMailUpdatedRequest { PlayerId = playerId });
                        _logger.LogInformation("게임 에이전트에 우편함 갱신 신호 전달: {PlayerId} Success={Success}",
                            playerId, response.Success);
                    }
                    break;

                case GameServer.Common.ServerType.Login:
                    // 의도적으로 무시 - 로그인 서버는 클라 관리 권한을 아주 찰나만
                    // 점유하는 특수 상태라, 이 시점에 메시지를 주고받게 하면 보안상
                    // 심각한 문제가 될 수 있음. 다음 접속(로비 진입) 시 자동 제공이
                    // 안전망이므로 그냥 넘어간다.
                    _logger.LogInformation("로그인 중인 세션 - 우편함 알림 의도적 생략: {PlayerId}", playerId);
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
}
