using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using SmartProperty.Domain.Identity;

namespace SmartProperty.Application.Authentication.OneTimeCodes;

/// <summary>
/// The approved rules for one-time codes — six digits, five minutes, five wrong attempts, one-minute resend
/// cooldown — and the code generation and hashing they need. The same rules apply to every purpose.
/// </summary>
internal static class OneTimeCodePolicy
{
    public const int CodeLength = 6;
    public static readonly TimeSpan CodeLifetime = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan ResendCooldown = TimeSpan.FromSeconds(60);

    /// <summary>How long the verified-reset token stays usable once the reset code has been entered correctly.</summary>
    public static readonly TimeSpan ResetGrantLifetime = TimeSpan.FromMinutes(10);

    private const int GrantTokenBytes = 32;
    private const int GrantTokenLength = 43;

    public static string GenerateCode()
    {
        return RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6", CultureInfo.InvariantCulture);
    }

    // Bound to the user and the purpose, so a code issued for one purpose or user never matches another. A
    // six-digit code is guessable offline from a leaked hash; the five-attempt limit and the short lifetime are
    // what protect it online.
    public static string HashCode(Guid userId, OneTimeCodePurpose purpose, string code)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes($"{userId:N}:{(int)purpose}:{code}"));

        return Convert.ToHexString(bytes);
    }

    public static bool CodeMatches(Guid userId, OneTimeCodePurpose purpose, string submittedCode, string storedHash)
    {
        var submitted = Encoding.UTF8.GetBytes(HashCode(userId, purpose, submittedCode));
        var stored = Encoding.UTF8.GetBytes(storedHash);

        return CryptographicOperations.FixedTimeEquals(submitted, stored);
    }

    /// <summary>A 256-bit random token in URL-safe base64, 43 characters.</summary>
    public static string GenerateGrantToken()
    {
        return Convert.ToBase64String(RandomNumberGenerator.GetBytes(GrantTokenBytes))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    public static bool IsWellFormedGrantToken(string? token)
    {
        return token is { Length: GrantTokenLength }
            && token.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_');
    }

    public static OneTimeCode IssueGrant(Guid userId, string token, DateTimeOffset now)
    {
        return new OneTimeCode(
            Guid.NewGuid(),
            userId,
            OneTimeCodePurpose.PasswordResetGrant,
            HashCode(userId, OneTimeCodePurpose.PasswordResetGrant, token),
            now,
            now.Add(ResetGrantLifetime));
    }

    public static bool IsWellFormedCode(string? code)
    {
        return code is { Length: CodeLength } && code.All(char.IsAsciiDigit);
    }

    public static OneTimeCode Issue(Guid userId, OneTimeCodePurpose purpose, string code, DateTimeOffset now)
    {
        return new OneTimeCode(
            Guid.NewGuid(),
            userId,
            purpose,
            HashCode(userId, purpose, code),
            now,
            now.Add(CodeLifetime));
    }

    public static string BuildPhoneMessage(string code)
    {
        return $"Your SmartProperty verification code is {code}. It expires in {Minutes} minutes.";
    }

    public static (string Subject, string Body) BuildEmailVerificationMessage(string code)
    {
        return (
            "Verify your email for SmartProperty",
            $"Your SmartProperty email verification code is {code}.\n\nIt expires in {Minutes} minutes. " +
            "If you did not create an account, you can ignore this email.");
    }

    public static (string Subject, string Body) BuildPasswordResetMessage(string code)
    {
        return (
            "Reset your SmartProperty password",
            $"Your SmartProperty password reset code is {code}.\n\nIt expires in {Minutes} minutes. " +
            "If you did not ask to reset your password, you can ignore this email; your password has not changed.");
    }

    private static int Minutes => (int)CodeLifetime.TotalMinutes;
}
