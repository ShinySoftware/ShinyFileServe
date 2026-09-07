using System.Security.Cryptography;

namespace ShinyFileServe.Core.Services;

/// <summary>PBKDF2-SHA256. No third-party dependency, and available on every head including the GTK4 one.</summary>
public static class PasswordHasher
{
    public const int DefaultIterations = 210_000;
    const int SaltBytes = 16;
    const int HashBytes = 32;

    public static (string Hash, string Salt, int Iterations) Hash(string password, int iterations = DefaultIterations)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltBytes);
        var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, HashBytes);
        return (Convert.ToBase64String(hash), Convert.ToBase64String(salt), iterations);
    }

    public static bool Verify(string password, string hash, string salt, int iterations)
    {
        if (String.IsNullOrEmpty(hash) || String.IsNullOrEmpty(salt))
            return false;

        try
        {
            var expected = Convert.FromBase64String(hash);
            var saltBytes = Convert.FromBase64String(salt);
            var actual = Rfc2898DeriveBytes.Pbkdf2(password, saltBytes, iterations, HashAlgorithmName.SHA256, expected.Length);
            return CryptographicOperations.FixedTimeEquals(expected, actual);
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
