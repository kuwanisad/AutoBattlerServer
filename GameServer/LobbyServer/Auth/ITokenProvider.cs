namespace LobbyServer.Auth;

/// <summary>
/// 토큰 발급/검증을 담당하는 인터페이스. 지금은 DummyTokenProvider(임시, 실질적 검증 없음)만
/// 있고, 나중에 실제 JWT 라이브러리 기반 구현체로 교체될 자리 - 구현체가 바뀌어도 이
/// 인터페이스를 쓰는 코드(LobbyControlService 등)는 안 바뀐다.
/// </summary>
public interface ITokenProvider
{
    /// <summary>주어진 playerId에 대한 토큰을 발급한다.</summary>
    string GenerateToken(string playerId);

    /// <summary>토큰을 검증하고, 유효하면 playerId를 꺼낸다.</summary>
    bool ValidateToken(string token, out string playerId);
}