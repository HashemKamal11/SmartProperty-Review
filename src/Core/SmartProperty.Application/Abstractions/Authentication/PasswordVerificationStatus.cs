namespace SmartProperty.Application.Abstractions.Authentication;

/// <summary>
/// Framework-neutral outcome of verifying a password against an encoded hash.
/// </summary>
public enum PasswordVerificationStatus
{
    Failed = 0,
    Success = 1,

    /// <summary>
    /// The password matches, but the hash was produced with outdated parameters and should be re-hashed.
    /// </summary>
    SuccessRehashNeeded = 2
}
