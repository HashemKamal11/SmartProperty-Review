#nullable enable
using Microsoft.AspNetCore.Identity;
using SmartProperty.Application.Abstractions.Authentication;

namespace SmartProperty.Api.Infrastructure.Authentication;

/// <summary>
/// Delegates to the framework PasswordHasher (PBKDF2 with a per-password random salt, versioned hash format,
/// fixed-time comparison). Only the hashing component is used; no ASP.NET Core Identity stores or managers.
/// </summary>
internal sealed class PasswordHasher : IPasswordHasher
{
    // The framework hasher's user parameter is unused by its algorithm; a shared placeholder satisfies the API.
    private static readonly object HashingSubject = new();

    private readonly PasswordHasher<object> _hasher = new();

    public string Hash(string password)
    {
        ArgumentException.ThrowIfNullOrEmpty(password);

        return _hasher.HashPassword(HashingSubject, password);
    }

    public bool Verify(string password, string passwordHash)
    {
        ArgumentException.ThrowIfNullOrEmpty(password);
        ArgumentException.ThrowIfNullOrWhiteSpace(passwordHash);

        PasswordVerificationResult result;

        try
        {
            result = _hasher.VerifyHashedPassword(HashingSubject, passwordHash, password);
        }
        catch (FormatException)
        {
            // The framework decodes the stored hash as Base64 and throws when it is not valid Base64.
            // A malformed persisted hash must fail verification rather than surface as an unhandled error.
            return false;
        }

        return result is PasswordVerificationResult.Success or PasswordVerificationResult.SuccessRehashNeeded;
    }
}
