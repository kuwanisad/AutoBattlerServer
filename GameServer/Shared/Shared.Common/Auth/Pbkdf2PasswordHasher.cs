using System.Security.Cryptography;

namespace Shared.Common.Auth;

/// <summary>
/// PBKDF2(SHA-256) 기반 구현체. 새 NuGet 패키지 없이 .NET 기본 클래스 라이브러리
/// (System.Security.Cryptography)만으로 동작한다.
///
/// 저장 형식: "{반복횟수}.{솔트(Base64)}.{해시(Base64)}" - 반복 횟수를 같이 저장해두면,
/// 나중에 권장 반복 횟수가 올라가도 기존에 저장된 해시는 그때 저장된 반복 횟수 그대로
/// 검증 가능하고(하위 호환), 신규 발급분만 새 반복 횟수를 쓰면 된다(마이그레이션 없이
/// 점진적 전환 가능).
/// </summary>
public sealed class Pbkdf2PasswordHasher : IPasswordHasher
{
    private const int SaltSizeBytes = 16;   // 128비트
    private const int HashSizeBytes = 32;   // 256비트
    private const int Iterations = 100_000; // OWASP 권장 최소치 근사(PBKDF2-SHA256 기준)

    public string Hash(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltSizeBytes);
        var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA256, HashSizeBytes);

        return $"{Iterations}.{Convert.ToBase64String(salt)}.{Convert.ToBase64String(hash)}";
    }

    public bool Verify(string password, string hashedPassword)
    {
        var parts = hashedPassword.Split('.');
        if (parts.Length != 3) return false;
        if (!int.TryParse(parts[0], out var iterations)) return false;

        byte[] salt, expectedHash;
        try
        {
            salt = Convert.FromBase64String(parts[1]);
            expectedHash = Convert.FromBase64String(parts[2]);
        }
        catch (FormatException)
        {
            return false;
        }

        var actualHash = Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, expectedHash.Length);

        // 상수 시간 비교 - 일반 == 이나 SequenceEqual은 앞에서부터 몇 바이트가 일치하는지에
        // 따라 비교 시간이 미묘하게 달라질 수 있어서(타이밍 공격 이론상 가능), 시간이
        // 항상 동일한 이 메서드를 쓴다.
        return CryptographicOperations.FixedTimeEquals(actualHash, expectedHash);
    }
}
