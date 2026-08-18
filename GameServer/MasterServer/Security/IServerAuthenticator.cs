using Grpc.Core;

namespace MasterServer.Security;

/// <summary>
/// 마스터 서버에 접속하는 서버(로비/매칭/게임 에이전트/채팅)가 신뢰할 수 있는 서버인지 검증한다.
///
/// 이 인터페이스 하나 뒤에서 여러 검증 수단(공유 시크릿, mTLS, IP 허용목록 등)을
/// 조합할 수 있도록 퍼사드로 설계했다. 지금은 <see cref="DummyServerAuthenticator"/>로
/// 항상 통과시키고, 나중에 mTLS 등을 도입할 때 <see cref="CompositeServerAuthenticator"/>
/// (여러 검증기를 조합) 같은 구현체로 교체하면 된다. 호출부(ConnectionHandler)는
/// 뒤에서 몇 개의 수단이 조합되는지 몰라도 된다.
/// </summary>
public interface IServerAuthenticator
{
    AuthenticationResult Authenticate(ServerCallContext context);
}

/// <summary>인증 결과. 실패 시 사유를 남겨 로그/오류 응답에 활용한다.</summary>
public readonly record struct AuthenticationResult(bool IsAuthenticated, string? FailureReason = null)
{
    public static AuthenticationResult Success() => new(true);
    public static AuthenticationResult Fail(string reason) => new(false, reason);
}