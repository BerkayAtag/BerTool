using BerTool.Shared.Contracts;
using System.Security.Cryptography;
using System.Text;

namespace BerTool.Server.Security;

internal static class PasswordHasher
{
    private const int DefaultIterations = 210_000;
    private const int SaltBytes = 16;
    private const int HashBytes = 32;

    public static PasswordHashRecord Hash(string password, int iterations = DefaultIterations)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(password);

        var salt = RandomNumberGenerator.GetBytes(SaltBytes);
        var hash = Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(password),
            salt,
            iterations,
            HashAlgorithmName.SHA256,
            HashBytes);

        return new PasswordHashRecord
        {
            Iterations = iterations,
            Salt = Convert.ToBase64String(salt),
            Hash = Convert.ToBase64String(hash)
        };
    }

    public static bool Verify(string password, PasswordHashRecord? record)
    {
        if (record is null ||
            string.IsNullOrWhiteSpace(password) ||
            string.IsNullOrWhiteSpace(record.Salt) ||
            string.IsNullOrWhiteSpace(record.Hash))
        {
            return false;
        }

        // Bozuk veya kötü niyetli kayıtlarda aşırı CPU kullanımını engelle.
        if (record.Iterations is < 10_000 or > 2_000_000)
        {
            return false;
        }

        try
        {
            var salt = Convert.FromBase64String(record.Salt);
            var expected = Convert.FromBase64String(record.Hash);

            // PBKDF2 çıktısı en az 16, en fazla 64 bayt olmalıdır.
            if (salt.Length is < 8 or > 64 || expected.Length is < 16 or > 64)
            {
                return false;
            }

            var actual = Rfc2898DeriveBytes.Pbkdf2(
                Encoding.UTF8.GetBytes(password),
                salt,
                record.Iterations,
                HashAlgorithmName.SHA256,
                expected.Length);

            return CryptographicOperations.FixedTimeEquals(actual, expected);
        }
        catch (FormatException)
        {
            return false;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    public static string HashAgentApiKey(string apiKey)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(apiKey));
        return Convert.ToHexString(hash);
    }

    public static bool VerifyAgentApiKey(string apiKey, string expectedHash)
    {
        if (string.IsNullOrWhiteSpace(apiKey) || string.IsNullOrWhiteSpace(expectedHash))
        {
            return false;
        }

        var actualHash = HashAgentApiKey(apiKey);
        if (actualHash.Length != expectedHash.Length)
        {
            return false;
        }

        return CryptographicOperations.FixedTimeEquals(
            Encoding.ASCII.GetBytes(actualHash),
            Encoding.ASCII.GetBytes(expectedHash));
    }

    public static string GenerateSecret(int bytes = 32)
    {
        return Convert.ToHexString(RandomNumberGenerator.GetBytes(bytes));
    }
}

internal sealed class PasswordHashRecord
{
    public string Algorithm { get; set; } = "PBKDF2-SHA256";
    public int Iterations { get; set; } = 210_000;
    public string Salt { get; set; } = string.Empty;
    public string Hash { get; set; } = string.Empty;
}

internal static class PasswordPolicy
{
    public const int MinimumLength = 12;

    public static string? Validate(string password)
    {
        if (string.IsNullOrWhiteSpace(password))
        {
            return "Yeni şifre boş olamaz.";
        }

        if (password.Length < MinimumLength)
        {
            return $"Yeni şifre en az {MinimumLength} karakter olmalıdır.";
        }

        if (!password.Any(char.IsLetter) || !password.Any(char.IsDigit))
        {
            return "Yeni şifre en az bir harf ve bir rakam içermelidir.";
        }

        return null;
    }
}
