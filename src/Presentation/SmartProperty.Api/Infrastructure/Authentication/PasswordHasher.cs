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

    // Non-secret literal used only to produce the throwaway hash that PerformDummyVerification verifies against.
    // It is not a credential: no account is ever created with it, and the hash it produces is never persisted.
    private const string DummyVerificationPassword = "smartproperty-dummy-verification-password";

    private readonly PasswordHasher<object> _hasher = new();

    // Produced once per hasher instance (registered as a singleton), so a login against a missing account pays for
    // one verification rather than a hash plus a verification. Never persisted, logged, or exposed.
    private readonly string _dummyPasswordHash;

    public PasswordHasher()
    {
        _dummyPasswordHash = _hasher.HashPassword(HashingSubject, DummyVerificationPassword);
    }

    public string Hash(string password)
    {
        ArgumentException.ThrowIfNullOrEmpty(password);

        return _hasher.HashPassword(HashingSubject, password);
    }

    public PasswordVerificationStatus Verify(string password, string passwordHash)
    {
        ArgumentException.ThrowIfNullOrEmpty(password);

        // The hash is persisted credential data, not caller input. An unusable stored hash fails closed.
        if (string.IsNullOrWhiteSpace(passwordHash))
        {
            return PasswordVerificationStatus.Failed;
        }

        PasswordVerificationResult result;

        try
        {
            result = _hasher.VerifyHashedPassword(HashingSubject, passwordHash, password);
        }
        catch (FormatException)
        {
            // The framework decodes the stored hash as Base64 and throws when it is not valid Base64.
            // A malformed persisted hash must fail verification rather than surface as an unhandled error.
            return PasswordVerificationStatus.Failed;
        }

        return result switch
        {
            PasswordVerificationResult.Success => PasswordVerificationStatus.Success,
            PasswordVerificationResult.SuccessRehashNeeded => PasswordVerificationStatus.SuccessRehashNeeded,
            // Failed, and any unrecognized framework value, fails closed.
            _ => PasswordVerificationStatus.Failed
        };
    }

    public void PerformDummyVerification(string password)
    {
        ArgumentException.ThrowIfNullOrEmpty(password);

        // Same framework call, same hasher configuration, and a well-formed current-format hash, so this costs the
        // same PBKDF2 work as verifying a real stored hash. The result is deliberately discarded: the caller has no
        // credential to authenticate, and the dummy hash belongs to no account.
        _ = _hasher.VerifyHashedPassword(HashingSubject, _dummyPasswordHash, password);
    }
}
