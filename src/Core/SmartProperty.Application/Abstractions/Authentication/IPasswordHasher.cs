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
}
