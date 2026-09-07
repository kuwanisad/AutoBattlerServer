using Grpc.Core;
using Grpc.Core.Interceptors;
using Microsoft.Extensions.Logging;
using Shared.Common.Auth;

namespace LobbyServer.Auth;

/// <summary>
/// 모든 gRPC 요청에서 토큰을 자동으로 검증하는 인터셉터.
/// Login/CreateAccount는 코드에서 제거됐다 - 로그인 서버가 최초 진입을 전담한다.
/// 지금은 EnterLobby부터 토큰이 필요하다.
///
/// 예외: ServerToServerMethods에 있는 메서드는 검증을 건너뛴다 - 플레이어가 아니라
/// 다른 백엔드 서버(하위 서버 등)가 부르는 RPC라 애초에 플레이어 토큰이 없다.
/// NotifyMailUpdated가 첫 사례(우편함 시스템 - DummySubServer가 호출).
/// </summary>
public sealed class AuthInterceptor : Interceptor
{
    private static readonly HashSet<string> ServerToServerMethods = new()
    {
        "/LobbyControl/NotifyMailUpdated",
        "/LobbyControl/RegisterIncomingTransfers",
    };

    private readonly ITokenProvider _tokenProvider;
    private readonly ILogger<AuthInterceptor> _logger;

    public AuthInterceptor(ITokenProvider tokenProvider, ILogger<AuthInterceptor> logger)
    {
        _tokenProvider = tokenProvider;
        _logger = logger;
    }

    public override async Task<TResponse> UnaryServerHandler<TRequest, TResponse>(
        TRequest request,
        ServerCallContext context,
        UnaryServerMethod<TRequest, TResponse> continuation)
    {
        if (!ServerToServerMethods.Contains(context.Method))
        {
            ValidateToken(context);
        }
        return await continuation(request, context);
    }

    public override async Task ServerStreamingServerHandler<TRequest, TResponse>(
        TRequest request,
        IServerStreamWriter<TResponse> responseStream,
        ServerCallContext context,
        ServerStreamingServerMethod<TRequest, TResponse> continuation)
    {
        if (!ServerToServerMethods.Contains(context.Method))
        {
            ValidateToken(context);
        }
        await continuation(request, responseStream, context);
    }

    private void ValidateToken(ServerCallContext context)
    {
        var authHeader = context.RequestHeaders
            .FirstOrDefault(h => h.Key == "authorization")?.Value;

        if (string.IsNullOrEmpty(authHeader) || !authHeader.StartsWith("Bearer "))
        {
            _logger.LogWarning("인증 토큰 없음: {Method}", context.Method);
            throw new RpcException(new Status(StatusCode.Unauthenticated, "토큰이 필요합니다."));
        }

        var token = authHeader.Substring("Bearer ".Length);

        if (!_tokenProvider.ValidateToken(token, out var playerId))
        {
            _logger.LogWarning("유효하지 않은 토큰: {Method}", context.Method);
            throw new RpcException(new Status(StatusCode.Unauthenticated, "유효하지 않은 토큰입니다."));
        }

        // 이후 핸들러에서 꺼낼 수 있도록 저장. 원본 토큰 문자열도 같이 남겨두는 이유:
        // EnterLobby가 이걸로 세션의 CurrentToken/PreviousToken과 대조해야 하는데(재접속
        // 시 "방금 밀려난 값"까지 허용하는 2슬롯 정책), playerId만으로는 그 대조를 할 수
        // 없다 - 여기서 이미 파싱된 토큰을 재사용하지 않으면 핸들러가 헤더를 다시
        // 파싱해야 하는 중복이 생긴다.
        context.UserState["playerId"] = playerId;
        context.UserState["token"] = token;
    }
}
