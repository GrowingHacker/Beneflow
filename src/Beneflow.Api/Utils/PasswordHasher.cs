using System.Security.Cryptography;

namespace Beneflow.Api.Utils;

/// <summary>
/// 密码哈希（PBKDF2-HMAC-SHA256）。
/// </summary>
public static class PasswordHasher
{
    private const int Iterations = 600_000;
    private const int SaltSize = 32;
    private const int KeySize = 32;

    public static string NewSalt()
    {
        var buf = RandomNumberGenerator.GetBytes(SaltSize);
        return Convert.ToHexString(buf).ToLowerInvariant();
    }

    public static string Hash(string password, string saltHex)
    {
        var salt = Convert.FromHexString(saltHex);
        using var pbkdf2 = new Rfc2898DeriveBytes(password, salt, Iterations, HashAlgorithmName.SHA256);
        return Convert.ToHexString(pbkdf2.GetBytes(KeySize)).ToLowerInvariant();
    }

    public static bool Verify(string password, string saltHex, string storedHash)
    {
        if (string.IsNullOrEmpty(saltHex) || string.IsNullOrEmpty(storedHash)) return false;
        try { return CryptographicOperations.FixedTimeEquals(
            Convert.FromHexString(Hash(password, saltHex)),
            Convert.FromHexString(storedHash)); }
        catch (FormatException) { return false; }
    }
}
