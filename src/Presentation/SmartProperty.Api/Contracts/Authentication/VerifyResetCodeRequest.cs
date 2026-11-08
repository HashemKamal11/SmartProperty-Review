#nullable enable
namespace SmartProperty.Api.Contracts.Authentication;

/// <summary>
/// Step two of a password reset: the six-digit code from the email. Members are nullable so a missing value is
/// reported by Application validation (422).
/// </summary>
public sealed record VerifyResetCodeRequest(string? Email, string? Code)
{
    // Keeps the email and the one-time code out of logs and debugger displays.
    public override string ToString()
    {
        return $"{nameof(VerifyResetCodeRequest)} {{ }}";
    }
}

/// <summary>
/// The code was right. <c>resetToken</c> is what the new-password step sends; it works once and until <c>expiresAt</c>.
/// </summary>
public sealed record VerifyResetCodeResponse(string ResetToken, DateTimeOffset ExpiresAt)
{
    public override string ToString()
    {
        return $"{nameof(VerifyResetCodeResponse)} {{ }}";
    }
}
