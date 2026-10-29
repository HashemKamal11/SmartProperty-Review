using System.Security.Cryptography;
using System.Text;
using SmartProperty.Domain.Identity;

namespace SmartProperty.Application.Authentication.PhoneVerifications;

/// <summary>
/// The approved rules for phone verification codes, and the code generation and hashing they need.
/// </summary>
internal static class PhoneVerificationPolicy
{
    public const int CodeLength = 6;
    public static readonly TimeSpan CodeLifetime = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan ResendCooldown = TimeSpan.FromSeconds(60);

    public static string GenerateCode()
    {
        return RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6", System.Globalization.CultureInfo.InvariantCulture);
    }

    // Bound to the user so the same code hashes differently for different users. A six-digit code is guessable
    // offline from a leaked hash; the five-attempt limit and the short lifetime are what protect it online.
    public static string HashCode(Guid userId, string code)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes($"{userId:N}:{code}"));

        return Convert.ToHexString(bytes);
    }

    public static bool CodeMatches(Guid userId, string submittedCode, string storedHash)
    {
        var submitted = Encoding.UTF8.GetBytes(HashCode(userId, submittedCode));
        var stored = Encoding.UTF8.GetBytes(storedHash);

        return CryptographicOperations.FixedTimeEquals(submitted, stored);
    }

    public static bool IsWellFormedCode(string? code)
    {
        return code is { Length: CodeLength } && code.All(char.IsAsciiDigit);
    }

    public static string BuildMessage(string code)
    {
        return $"Your SmartProperty verification code is {code}. It expires in {(int)CodeLifetime.TotalMinutes} minutes.";
    }

    public static PhoneVerification Issue(Guid userId, string code, DateTimeOffset now)
    {
        return new PhoneVerification(
            Guid.NewGuid(),
            userId,
            HashCode(userId, code),
            now,
            now.Add(CodeLifetime));
    }
}
