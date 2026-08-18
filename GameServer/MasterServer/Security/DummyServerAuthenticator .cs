using Grpc.Core;

namespace MasterServer.Security;

/// <summary>
/// 항상 인증을 통과시키는 더미 구현체. 실제 검증(mTLS 등) 붙이기 전까지 사용한다.
/// 다른 더미 구현체들(DummyTokenProvider, DummyChatFilter 등)과 같은 패턴.
/// </summary>
public sealed class DummyServerAuthenticator : IServerAuthenticator
{
    public AuthenticationResult Authenticate(ServerCallContext context) => AuthenticationResult.Success();
}