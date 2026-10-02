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
    private readonly LoginAccountServerIntermediary _accountServer;
    private readonly ILogger _logger;

    internal LoginConnectionHandler(
        MasterConnectionService masterConnection,
        ISessionRegistry sessionRegistry,
        ITokenProvider tokenProvider,
        LoginAccountServerIntermediary accountServer,
        ILogger logger)
    {
        _masterConnection = masterConnection;
        _sessionRegistry = sessionRegistry;
        _tokenProvider = tokenProvider;
        _accountServer = accountServer;
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

        if (requestStream.Current.PayloadCase == ClientToLoginMessage.PayloadOneofCase.Reconnect)
        {
            await HandleReconnectAsync(requestStream.Current.Reconnect, responseStream, cancellationToken);
            return;
        }

        if (requestStream.Current.PayloadCase == ClientToLoginMessage.PayloadOneofCase.CreateAccount)
        {
            var createRequest = requestStream.Current.CreateAccount;

            if (string.IsNullOrEmpty(createRequest.AccountId) || string.IsNullOrEmpty(createRequest.Password) ||
                string.IsNullOrEmpty(createRequest.Nickname))
            {
                await TrySendRejectedAsync(responseStream, "INVALID_CREDENTIALS", cancellationToken);
                return;
            }

            // 3. 계정 서버에 생성 요청(ID/닉네임 중복 검사 포함)
            var newPlayerId = await ResolveNewAccountAsync(createRequest, cancellationToken);
            if (newPlayerId == null)
            {
                await TrySendRejectedAsync(responseStream, "ACCOUNT_CREATION_FAILED", cancellationToken);
                return;
            }

            // 생성 성공 = 그 즉시 로그인 성공으로 간주 - Credentials 로그인과
            // 완전히 같은 절차(4~7단계)를 그대로 탄다. 닉네임은 이미 요청 자체에
            // 있으니 계정 서버에 다시 물어볼 필요 없음.
            await CompleteLoginAsync(newPlayerId, createRequest.Nickname, responseStream, cancellationToken);
            return;
        }

        if (requestStream.Current.PayloadCase != ClientToLoginMessage.PayloadOneofCase.Credentials)
        {
            _logger.LogWarning("첫 메시지가 Credentials/Reconnect/CreateAccount가 아님: {Peer}", context.Peer);
            await TrySendRejectedAsync(responseStream, "INVALID_CREDENTIALS", cancellationToken);
            return;
        }

        credentials = requestStream.Current.Credentials;

        if (string.IsNullOrEmpty(credentials.AccountId) || string.IsNullOrEmpty(credentials.Password))
        {
            await TrySendRejectedAsync(responseStream, "INVALID_CREDENTIALS", cancellationToken);
            return;
        }

        // 3. 계정 서버에 대조 요청(더 이상 자동 생성 안 함 - ResolvePlayerIdAsync 주석 참고)
        var playerId = await ResolvePlayerIdAsync(credentials, cancellationToken);
        if (playerId == null)
        {
            await TrySendRejectedAsync(responseStream, "INVALID_CREDENTIALS", cancellationToken);
            return;
        }

        var nickname = await ResolveNicknameAsync(playerId, cancellationToken);
        await CompleteLoginAsync(playerId, nickname, responseStream, cancellationToken);
    }

    /// <summary>4~7단계(세션 레지스트리 관리 권한 획득 → 매칭 서버에 로비 주소
    /// 요청 → 로비 서버로 소유권 이관 → 클라에 리다이렉트 응답) - 기존
    /// 계정으로 로그인했든(Credentials) 방금 막 계정을 생성했든(CreateAccountRequest)
    /// 이 지점부터는 완전히 같은 절차다(둘 다 "이제부터 로그인된 상태"이므로).
    /// nickname은 호출부가 미리 알아내서 넘긴다 - CreateAccountRequest 쪽은
    /// 요청 자체에 이미 있고(ResolveNewAccountAsync), Credentials 쪽은 계정
    /// 서버에 따로 물어봐야 한다(ResolveNicknameAsync).</summary>
    private async Task CompleteLoginAsync(
        string playerId,
        string nickname,
        IServerStreamWriter<LoginToClientMessage> responseStream,
        CancellationToken cancellationToken)
    {
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
            // Status와 다르게 이건 "지금 실제 소유자"를 나타내야 하므로 Login으로 채운다 -
            // RemoteMailNotifier가 이 값을 보고 로그인 중인 세션한테는 의도적으로 메시지를
            // 안 보내게 하는 근거가 됨(보안 - 로그인 서버는 아주 찰나만 권한을 점유하는
            // 특수 상태).
            OwnerServerType = GameServer.Common.ServerType.Login,
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
        newSession.OwnerServerType = GameServer.Common.ServerType.Lobby;
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
                DestinationServerType = GameServer.Common.ServerType.Lobby,
                Nickname = nickname,
            },
        }, cancellationToken);

        // 7. 스트림 종료 - 클라는 이 로그인 서버로 다시 안 온다.
    }

    /// <summary>
    /// 갈 곳을 잃은 클라의 재진입 처리 - "안정적 단일 조회 창구" 역할(KNOWN_ISSUES.md 참고).
    /// 세션이 살아있는 소유자를 갖고 있을 때만 그 소유자 주소로 안내하고, 소유권 자체는
    /// 안 건드린다(로그인 서버가 다시 클레임할 필요가 없음 - 그냥 "가라"고 알려주기만
    /// 함). 세션이 없거나 소유 서버가 죽어있으면 자격증명으로 처음부터 다시 로그인하라고
    /// 거부한다 - 대조할 세션이 없는 상태에서 토큰 문자열만으로 신원을 인정하면 검증
    /// 불가능한 사칭 구멍이 생기기 때문(설계 논의 중 확인됨).
    /// </summary>
    private async Task HandleReconnectAsync(
        ReconnectRequest request,
        IServerStreamWriter<LoginToClientMessage> responseStream,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(request.PlayerId) || string.IsNullOrEmpty(request.Token))
        {
            await TrySendRejectedAsync(responseStream, "INVALID_CREDENTIALS", cancellationToken);
            return;
        }

        OwnerInfo? owner;
        try
        {
            owner = await _sessionRegistry.GetOwnerAsync(request.PlayerId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "재진입 처리 중 세션 레지스트리 조회 실패: {PlayerId}", request.PlayerId);
            await TrySendRejectedAsync(responseStream, "INTERNAL_ERROR", cancellationToken);
            return;
        }

        if (owner == null || !owner.IsAlive || string.IsNullOrEmpty(owner.Address))
        {
            // 세션 자체가 없거나, 있어도 소유 서버의 하트비트가 이미 만료됨 - 대조할
            // 대상이 없으므로 재진입 자체를 거부. 처음부터 다시 로그인해야 함.
            _logger.LogInformation("재진입 실패 - 살아있는 소유자 없음: {PlayerId}", request.PlayerId);
            await TrySendRejectedAsync(responseStream, "NO_ACTIVE_SESSION", cancellationToken);
            return;
        }

        var session = await _sessionRegistry.GetAsync(request.PlayerId);
        if (session == null || !TokenVerification.IsValid(session, request.Token))
        {
            _logger.LogInformation("재진입 실패 - 토큰 불일치: {PlayerId}", request.PlayerId);
            await TrySendRejectedAsync(responseStream, "INVALID_TOKEN", cancellationToken);
            return;
        }

        var destinationServerType = owner.ServerType switch
        {
            GameServer.Common.ServerType.Lobby => GameServer.Common.ServerType.Lobby,
            GameServer.Common.ServerType.Ingame => GameServer.Common.ServerType.Ingame,
            _ => (GameServer.Common.ServerType?)null,
        };

        if (destinationServerType == null)
        {
            // Login 소유(로그인 중인 다른 세션과 경합) 등 재진입 안내 대상이 아닌 타입 -
            // 이 경우도 처음부터 다시 로그인하게 한다.
            _logger.LogInformation("재진입 실패 - 안내 불가능한 소유 서버 타입: {PlayerId} Type={Type}",
                request.PlayerId, owner.ServerType);
            await TrySendRejectedAsync(responseStream, "NO_ACTIVE_SESSION", cancellationToken);
            return;
        }

        // 로비는 owner.Address(=서버 자신의 gRPC 주소)가 곧 클라가 접속할 주소와
        // 같다. 인게임은 다르다 - owner.Address는 게임 에이전트 자신의 gRPC 주소(서버간
        // 통신용)라 클라가 NGO로 접속할 수 있는 주소가 아니다. 그 실제 주소는
        // session.CurrentServer에 있다(HandlePlayerJoinedAsync가 기록해둔 것).
        // 이 둘을 혼동해서 owner.Address를 그대로 안내했던 게 실제 버그였다
        // (재접속 테스트 중 발견 - KNOWN_ISSUES.md 참고).
        // 게임 에이전트의 ApprovalRequest 검증은 Status==Moving인 세션만 승인한다
        // (정상 매칭 흐름과 판단 기준을 하나로 유지하기 위함 - GameServerHandler.
        // HandleApprovalRequestAsync 참고). 재진입은 세션이 계속 Ingame 상태였던
        // 채로 오므로, 미리 Moving으로 바꿔주지 않으면 "잘못된 세션 상태"로 거부당한다
        // (재접속 테스트 중 발견 - KNOWN_ISSUES.md 참고). 로비 재진입은 이 검사가
        // 없어서 필요 없다 - 인게임일 때만 해준다.
        if (destinationServerType == GameServer.Common.ServerType.Ingame)
        {
            session.Status = PlayerStatus.Moving;
            await _sessionRegistry.UpdateAsync(request.PlayerId, session);
        }

        var redirectAddress = destinationServerType == GameServer.Common.ServerType.Ingame
            ? session.CurrentServer
            : owner.Address;

        if (string.IsNullOrEmpty(redirectAddress))
        {
            _logger.LogWarning(
                "재진입 실패 - 클라 접속 주소를 확인할 수 없음: {PlayerId} Type={Type}",
                request.PlayerId, destinationServerType);
            await TrySendRejectedAsync(responseStream, "NO_ACTIVE_SESSION", cancellationToken);
            return;
        }

        _logger.LogInformation("재진입 성공 - 안내: {PlayerId} → {Address} ({Type})",
            request.PlayerId, redirectAddress, destinationServerType);

        await TrySendAsync(responseStream, new LoginToClientMessage
        {
            Redirect = new LoginRedirect
            {
                LobbyAddress = redirectAddress,
                PlayerId = request.PlayerId,
                Token = session.CurrentToken,
                ChatAddress = _masterConnection.ChatServerAddress,
                DestinationServerType = destinationServerType.Value,
            },
        }, cancellationToken);
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

    /// <summary>계정 서버에 검증만 요청한다 - 계정이 없으면 더 이상 자동으로
    /// 만들지 않는다(예전엔 여기서 같은 정보로 즉시 생성했었는데, 그러면
    /// 닉네임을 입력받을 지점이 없었다 - 계정 생성은 이제
    /// CreateAccountRequest로 명시적으로 요청해야 한다, ResolveNewAccountAsync
    /// 참고).</summary>
    private async Task<string?> ResolvePlayerIdAsync(Credentials credentials, CancellationToken cancellationToken)
    {
        try
        {
            var verifyResponse = await _accountServer.VerifyAccountAsync(
                credentials.AccountId, credentials.Password, cancellationToken);

            return verifyResponse.Found ? verifyResponse.PlayerId : null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "계정 서버 호출 실패");
            return null;
        }
    }

    /// <summary>Credentials 로그인 경로에서만 쓰인다 - CreateAccountRequest
    /// 경로는 요청 자체에 이미 닉네임이 있어서 이 호출이 필요 없다. 계정
    /// 서버 호출이 실패해도 로그인 자체를 막지는 않는다(빈 문자열로 계속
    /// 진행) - 닉네임 표시 실패가 로그인 실패로 번질 이유는 없음.</summary>
    private async Task<string> ResolveNicknameAsync(string playerId, CancellationToken cancellationToken)
    {
        try
        {
            var response = await _accountServer.GetNicknamesAsync(new[] { playerId }, cancellationToken);
            return response.Nicknames.TryGetValue(playerId, out var nickname) ? nickname : string.Empty;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "닉네임 조회 실패: {PlayerId}", playerId);
            return string.Empty;
        }
    }

    /// <summary>CreateAccountRequest를 계정 서버로 그대로 전달한다. ID/닉네임
    /// 중복 검사는 전부 계정 서버(AccountControlService.CreateAccount)가
    /// 한다 - 여기는 그 결과를 로그인 흐름에 맞게 옮기기만 한다.</summary>
    private async Task<string?> ResolveNewAccountAsync(
        CreateAccountRequest request, CancellationToken cancellationToken)
    {
        try
        {
            var createResponse = await _accountServer.CreateAccountAsync(
                request.AccountId, request.Password, request.Nickname, cancellationToken);

            if (createResponse.Success)
            {
                _logger.LogInformation("신규 계정 생성: {AccountId} → {PlayerId}",
                    request.AccountId, createResponse.PlayerId);
                return createResponse.PlayerId;
            }

            _logger.LogInformation("계정 생성 실패: {AccountId} - {Error}",
                request.AccountId, createResponse.ErrorMessage);
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "계정 서버 호출 실패");
            return null;
        }
    }

    /// <summary>매칭 서버에 지금 갈 수 있는 로비 서버 주소를 요청한다. 로비가
    /// 여럿이면 매칭 서버가 알아서 하나를 골라준다(부하 분산 등 - 여기는
    /// 그 선택 기준을 몰라도 됨).</summary>
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