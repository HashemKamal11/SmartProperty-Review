namespace SmartProperty.Domain.Identity;

/// <summary>
/// What a one-time code proves. A code issued for one purpose never satisfies another.
/// </summary>
public enum OneTimeCodePurpose
{
    PhoneVerification = 1,
    EmailVerification = 2,
    PasswordReset = 3,

    /// <summary>
    /// The proof that a password reset code was entered correctly: a long random token, not a six-digit code, which the
    /// new-password step presents. Issued when the reset code is verified, valid for ten minutes, and used once.
    /// </summary>
    PasswordResetGrant = 4
}
