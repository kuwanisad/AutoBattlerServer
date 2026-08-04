namespace LobbyServer.Auth;

/// <summary>
/// JWT 토큰 생성 및 검증 담당.
/// gRPC와 무관한 순수 비즈니스 로직이라 동기 메서드로 충분하다.
/// DummyTokenProvider는 실제 서명 없이 더미 토큰을 생성한다.
/// 나중에 실제 JWT 라이브러리 기반 구현체로 교체할 때 이 인터페이스만 유지하면 된다.
/// </summary>
public interface ITokenProvider
{
    /// <summary>플레이어 ID로 토큰을 생성한다.</summary>
    string GenerateToken(string playerId);

    /// <summary>
    /// 토큰을 검증하고 플레이어 ID를 추출한다.
    /// 유효하지 않으면 false를 반환한다.
    /// </summary>
    bool ValidateToken(string token, out string playerId);
}
