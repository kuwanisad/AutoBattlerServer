using Grpc.Core;
using Grpc.Core.Interceptors;
using Microsoft.Extensions.Logging;

namespace LobbyServer.Auth;

/// <summary>
/// 모든 gRPC 요청에서 JWT 토큰을 자동으로 검증하는 인터셉터.
/// Login/CreateAccount는 토큰이 없어도 통과한다.
/// 나머지 요청은 Authorization 헤더에 "Bearer {token}"이 있어야 한다.
/// </summary>
public sealed class AuthInterceptor : Interceptor
{
    private readonly ITokenProvider _tokenProvider;
    private readonly ILogger<AuthInterceptor> _logger;

    // 토큰 없이 통과하는 메서드 목록
    private static readonly HashSet<string> PublicMethods =
    [
        "/LobbyControl/Login",
        "/LobbyControl/CreateAccount",
    ];

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
        if (!PublicMethods.Contains(context.Method))
            ValidateToken(context);

        return await continuation(request, context);
    }

    public override async Task ServerStreamingServerHandler<TRequest, TResponse>(
        TRequest request,
        IServerStreamWriter<TResponse> responseStream,
        ServerCallContext context,
        ServerStreamingServerMethod<TRequest, TResponse> continuation)
    {
        if (!PublicMethods.Contains(context.Method))
            ValidateToken(context);

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

        // 이후 핸들러에서 플레이어 ID를 꺼낼 수 있도록 저장
        context.UserState["playerId"] = playerId;
    }
}
