using System;
using System.Security.Cryptography;

namespace ScreenGuard;

/// <summary>
/// PBKDF2-SHA256 加盐哈希（12 万次迭代），比对用固定时间算法防时序侧信道。
/// </summary>
internal static class PasswordHasher
{
    private const int Iterations = 120_000;
    private const int SaltSize = 16;
    private const int KeySize = 32;

    public static (string Hash, string Salt) Create(string password)
    {
        byte[] salt = RandomNumberGenerator.GetBytes(SaltSize);
        byte[] key = Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA256, KeySize);
        return (Convert.ToBase64String(key), Convert.ToBase64String(salt));
    }

    public static bool Verify(string password, string? hashBase64, string? saltBase64)
    {
        if (string.IsNullOrEmpty(hashBase64) || string.IsNullOrEmpty(saltBase64))
            return false;

        try
        {
            byte[] salt = Convert.FromBase64String(saltBase64);
            byte[] expected = Convert.FromBase64String(hashBase64);
            byte[] actual = Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA256, expected.Length);
            return CryptographicOperations.FixedTimeEquals(actual, expected);
        }
        catch
        {
            return false;
        }
    }
}
