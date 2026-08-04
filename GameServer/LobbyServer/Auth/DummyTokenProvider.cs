namespace LobbyServer.Auth;

/// <summary>
/// 더미 토큰 생성/검증 구현체.
/// 실제 JWT 서명 없이 "dummy:{playerId}" 형태의 토큰을 생성한다.
/// 실제 JWT 라이브러리 기반 구현체로 교체 시 이 클래스만 교체하면 된다.
/// </summary>
public sealed class DummyTokenProvider : ITokenProvider
{
    private const string Prefix = "dummy:";

    public string GenerateToken(string playerId)
    {
        // 실제 JWT는 Header.Payload.Signature 구조지만
        // 더미에서는 단순히 prefix + playerId로 구성한다.
        return $"{Prefix}{playerId}";
    }

    public bool ValidateToken(string token, out string playerId)
    {
        playerId = string.Empty;  // null 대신 string.Empty

        if (string.IsNullOrEmpty(token) || !token.StartsWith(Prefix))
            return false;

        playerId = token.Substring(Prefix.Length);
        return !string.IsNullOrEmpty(playerId);
    }
}
