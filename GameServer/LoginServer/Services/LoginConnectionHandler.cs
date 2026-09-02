using Grpc.Core;
using Grpc.Net.Client;
using AccountServer;
using MainServer.Common.MasterConnection;
using MatchingServer;
using Microsoft.Extensions.Logging;
using Shared.Common;
using Shared.Common.Auth;
using Shared.Common.Redis;
using System.Net.Http;

namespace LoginServer.Services;

/// <summary>
/// Connect RPC의 실제 로직. 클라 하나의 접속을 처음부터 끝까지 담당한다.
///
/// 흐름 (설계 논의에서 확정된 7단계):
/// 1. 클라 접속 (Connect 스트림 오픈)
/// 2. 계정 정보 제출 대기 - 제한 시간 안에 안 오면 스트림 종료
/// 3. AccountServer에 계정 대조 → playerId 획득 (없으면 같은 정보로 계정 생성 시도)
/// 4. 세션 레지스트리에 관리 권한 획득 시도 - 다른 서버가 살아서 소유 중이면 거부
/// 5. 매칭 서버에 적절한 로비 서버 주소 요청
/// 6. 로비 서버로 소유권 자발적 이관(게임 에이전트의 CloseRoomAndRedirectAsync와 같은
///    패턴) 후 클라에게 리다이렉트 지시
/// 7. 스트림 종료 - 클라는 이 로그인 서버로 다시 안 온다(재접속/JWT 개념 없음)
///
/// 어느 단계에서든 연결이 끊기면 그냥 끊긴 걸로 끝 - 복구/재시도 로직 없음.
/// </summary>
internal sealed class LoginConnectionHandler
{
    // 계정 정보 제출 대기 제한 시간.
    private static readonly TimeSpan CredentialsTimeout = TimeSpan.FromSeconds(30);

    private readonly MasterConnectionService _masterConnection;
    private readonly ISessionRegistry _sessionRegistry;
    private readonly ITokenProvider _tokenProvider;
    private readonly ILogger _logger;

    internal LoginConnectionHandler(
        MasterConnectionService masterConnection,
        ISessionRegistry sessionRegistry,
        ITokenProvider tokenProvider,
        ILogger logger)
    {
        _masterConnection = masterConnection;
        _sessionRegistry = sessionRegistry;
        _tokenProvider = tokenProvider;
        _logger = logger;
    }

    internal async Task HandleAsync(
        IAsyncStreamReader<ClientToLoginMessage> requestStream,
        IServerStreamWriter<LoginToClientMessage> responseStream,
        ServerCallContext context)
    {
        var cancellationToken = context.CancellationToken;

        // 2. 계정 정보 제출 대기 (제한 시간)
        Credentials? credentials;
        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(CredentialsTimeout);

            if (!await requestStream.MoveNext(timeoutCts.Token))
            {
                _logger.LogInformation("클라 접속 종료(계정 정보 제출 전): {Peer}", context.Peer);
                return;
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogInformation("계정 정보 제출 시간 초과: {Peer}", context.Peer);
            await TrySendRejectedAsync(responseStream, "TIMEOUT", cancellationToken);
            return;
        }
        catch (OperationCanceledException)
        {
            return; // 클라가 연결 자체를 끊음
        }

        if (requestStream.Current.PayloadCase != ClientToLoginMessage.PayloadOneofCase.Credentials)
        {
            _logger.LogWarning("첫 메시지가 Credentials가 아님: {Peer}", context.Peer);
            await TrySendRejectedAsync(responseStream, "INVALID_CREDENTIALS", cancellationToken);
            return;
        }

        credentials = requestStream.Current.Credentials;

        if (string.IsNullOrEmpty(credentials.AccountId) || string.IsNullOrEmpty(credentials.Password))
        {
            await TrySendRejectedAsync(responseStream, "INVALID_CREDENTIALS", cancellationToken);
            return;
        }

        // 3. 계정 서버에 대조/생성 요청
        var playerId = await ResolvePlayerIdAsync(credentials, cancellationToken);
        if (playerId == null)
        {
            await TrySendRejectedAsync(responseStream, "INVALID_CREDENTIALS", cancellationToken);
            return;
        }

        // 4. 세션 레지스트리 관리 권한 획득 시도 (자기 자신으로 클레임)
        // 여기서 초기 토큰도 같이 발급해서 세션에 심어둔다 - 클라가 이후 로비에서
        // EnterLobby를 부를 때 이 토큰을 제출하면, 로비의 TokenVerification이 세션의
        // CurrentToken과 대조해서 통과시킨다(로그인 서버가 이미 심어둔 값과 같으므로).
        var initialToken = _tokenProvider.GenerateToken(playerId);

        var newSession = new PlayerSession
        {
            Status = PlayerStatus.Lobby, // 곧 로비로 보낼 것이므로 최종 상태를 미리 반영
            CurrentServer = _masterConnection.SelfAddress,
            CurrentToken = initialToken,
            TokenIssuedAtUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        };

        var claimResult = await _sessionRegistry.TryClaimOwnershipWithPreviousOwnerAsync(
            _masterConnection.InstanceId, playerId, newSession);

        if (!claimResult.Claimed)
        {
            _logger.LogInformation("세션 충돌로 로그인 거부: {PlayerId} (기존 소유자 {Owner})",
                playerId, claimResult.PreviousOwner?.ServerId);
            await TrySendRejectedAsync(responseStream, "SESSION_CONFLICT", cancellationToken);
            return;
        }

        // 5. 매칭 서버에 로비 주소 요청
        var lobby = await RequestLobbyAddressAsync(cancellationToken);
        if (lobby == null)
        {
            _logger.LogWarning("갈 수 있는 로비를 찾지 못함 - 로그인 거부: {PlayerId}", playerId);
            await TrySendRejectedAsync(responseStream, "NO_LOBBY_AVAILABLE", cancellationToken);
            return;
        }

        // 6. 로비 서버로 소유권 자발적 이관 (게임 에이전트의 CloseRoomAndRedirectAsync와 같은 패턴 -
        // 기존 소유자(이 로그인 서버)가 살아있는 채로 자발적으로 넘겨주는 것이므로
        // TryClaimOwnershipAsync의 "기존 소유자 생존 확인" 절차를 타면 오히려 거부당한다.
        // 대신 새 소유자로 직접 갱신한다.)
        newSession.Status = PlayerStatus.Lobby;
        newSession.CurrentServer = string.Empty;
        newSession.NextServer = lobby.Value.Address;
        newSession.OwnerServerId = lobby.Value.ServerId;
        await _sessionRegistry.UpdateAsync(playerId, newSession);

        _logger.LogInformation("로그인 성공, 로비로 이관: {PlayerId} → {Address}", playerId, lobby.Value.Address);

        await TrySendAsync(responseStream, new LoginToClientMessage
        {
            Redirect = new LoginRedirect
            {
                LobbyAddress = lobby.Value.Address,
                PlayerId = playerId,
                Token = initialToken,
                ChatAddress = _masterConnection.ChatServerAddress,
            },
        }, cancellationToken);

        // 7. 스트림 종료 - 클라는 이 로그인 서버로 다시 안 온다.
    }

    /// <summary>
    /// MasterConnectionService가 마스터 서버에 접속할 때 쓰는 것과 동일한 SocketsHttpHandler
    /// 구성으로 채널을 만든다. 원래는 GrpcChannel.ForAddress(address)만 써도 될 것 같았는데,
    /// AccountServer(TLS 없는 평문 HTTP/2, h2c) 호출에서 "HTTP_1_1_REQUIRED" 오류가 재현됐고
    /// 이 명시적 핸들러 구성으로 바꾸니 해결됐다 - 정확한 메커니즘은 불명확하지만, 이미
    /// 검증된 패턴을 그대로 재사용하는 쪽이 안전하다고 판단.
    /// </summary>
    private static GrpcChannel CreateChannel(string address)
    {
        var handler = new SocketsHttpHandler
        {
            EnableMultipleHttp2Connections = true,
            KeepAlivePingDelay = TimeSpan.FromSeconds(10),
            KeepAlivePingTimeout = TimeSpan.FromSeconds(5),
            KeepAlivePingPolicy = HttpKeepAlivePingPolicy.WithActiveRequests,
        };

        return GrpcChannel.ForAddress(address, new GrpcChannelOptions
        {
            HttpHandler = handler,
            DisposeHttpClient = true,
        });
    }

    /// <summary>계정 서버에 검증을 요청하고, 없으면 같은 정보로 생성까지 시도해서 playerId를 얻는다.</summary>
    private async Task<string?> ResolvePlayerIdAsync(Credentials credentials, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(_masterConnection.AccountServerAddress))
        {
            _logger.LogError("계정 서버 주소 없음 - 로그인 처리 불가");
            return null;
        }

        try
        {
            using var channel = CreateChannel(_masterConnection.AccountServerAddress);
            var client = new AccountControl.AccountControlClient(channel);

            var verifyResponse = await client.VerifyAccountAsync(new VerifyAccountRequest
            {
                AccountId = credentials.AccountId,
                Password = credentials.Password,
            }, cancellationToken: cancellationToken);

            if (verifyResponse.Found)
            {
                return verifyResponse.PlayerId;
            }

            // 계정이 없으면 같은 정보로 새로 만든다 (별도 "계정 생성 확인" 단계 없이 -
            // 지금은 임시로 간단하게 처리).
            var createResponse = await client.CreateAccountAsync(new CreateAccountRequest
            {
                AccountId = credentials.AccountId,
                Password = credentials.Password,
            }, cancellationToken: cancellationToken);

            if (createResponse.Success)
            {
                _logger.LogInformation("신규 계정 생성: {AccountId} → {PlayerId}",
                    credentials.AccountId, createResponse.PlayerId);
                return createResponse.PlayerId;
            }

            _logger.LogInformation("계정 생성 실패: {AccountId} - {Error}",
                credentials.AccountId, createResponse.ErrorMessage);
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "계정 서버 호출 실패");
            return null;
        }
    }

    private async Task<(string ServerId, string Address)?> RequestLobbyAddressAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(_masterConnection.MatchingServerAddress))
        {
            _logger.LogError("매칭 서버 주소 없음 - 로비 주소 요청 불가");
            return null;
        }

        try
        {
            using var channel = CreateChannel(_masterConnection.MatchingServerAddress);
            var client = new IngameMatchControl.IngameMatchControlClient(channel);
            var response = await client.RequestLobbyAddressAsync(
                new RequestLobbyAddressRequest(), cancellationToken: cancellationToken);

            return response.Found ? (response.LobbyServerId, response.Address) : null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "로비 주소 요청 실패");
            return null;
        }
    }

    private async Task TrySendRejectedAsync(
        IServerStreamWriter<LoginToClientMessage> responseStream, string reason, CancellationToken cancellationToken)
    {
        await TrySendAsync(responseStream, new LoginToClientMessage
        {
            Rejected = new LoginRejected { Reason = reason },
        }, cancellationToken);
    }

    private async Task TrySendAsync(
        IServerStreamWriter<LoginToClientMessage> responseStream,
        LoginToClientMessage message,
        CancellationToken cancellationToken)
    {
        try
        {
            await responseStream.WriteAsync(message, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "클라에 응답 전송 실패 - 이미 끊겼을 수 있음");
        }
    }
}
