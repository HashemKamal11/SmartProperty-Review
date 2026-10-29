namespace SmartProperty.Domain.Identity;

/// <summary>
/// What a one-time code proves. A code issued for one purpose never satisfies another.
/// </summary>
public enum OneTimeCodePurpose
{
    PhoneVerification = 1,
    EmailVerification = 2,
    PasswordReset = 3
}
