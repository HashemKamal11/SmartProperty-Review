namespace SmartProperty.Application.Abstractions.Authentication;

/// <summary>
/// Hashes and verifies passwords. Password strength policy is not the hasher's concern.
/// </summary>
public interface IPasswordHasher
{
    /// <summary>
    /// Returns the encoded hash representation of the password. The plaintext is never returned or stored.
    /// </summary>
    string Hash(string password);

    /// <summary>
    /// Verifies the password against the encoded hash. A null, empty, whitespace, or malformed hash yields
    /// <see cref="PasswordVerificationStatus.Failed"/>.
    /// </summary>
    PasswordVerificationStatus Verify(string password, string passwordHash);

    /// <summary>
    /// Runs the same verification work as <see cref="Verify"/> against an internal throwaway hash and discards the
    /// outcome. Callers use it where no stored credential exists, so that path costs the same as a real failed
    /// verification instead of returning early. It can never authenticate anyone.
    /// </summary>
    void PerformDummyVerification(string password);
}
