namespace Shared.Common.Auth;

/// <summary>
/// 비밀번호 해싱/검증. 절대 원문을 저장하지 않는다 - 해시는 되돌릴 수 없는 단방향
/// 변환이라, 서버조차도 원본 비밀번호를 다시 알아낼 방법이 없다(DB가 통째로 털려도
/// 공격자가 얻는 건 해시값뿐).
///
/// TLS(전송 구간 암호화)와는 완전히 다른 층위의 문제를 해결한다는 점에 주의 - TLS는
/// "네트워크 도청"을 막고, 이건 "저장소가 뚫렸을 때 원문 노출"을 막는다. 둘 다 필요하고
/// 서로 대체 관계가 아니다.
/// </summary>
public interface IPasswordHasher
{
    /// <summary>비밀번호를 해싱한다. 매번 다른 솔트를 써서 같은 비밀번호라도 결과가 매번 다르다.</summary>
    string Hash(string password);

    /// <summary>제출된 비밀번호가 저장된 해시와 일치하는지 확인한다.</summary>
    bool Verify(string password, string hashedPassword);
}
