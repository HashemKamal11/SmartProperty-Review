using SmartProperty.Application.Abstractions.Authentication;

namespace SmartProperty.UnitTests.TestDoubles;

/// <summary>
/// Records which verification path a handler took. The login timing hardening is asserted through these
/// counters — control flow, never elapsed time — so the tests stay deterministic on any machine.
/// </summary>
internal sealed class FakePasswordHasher : IPasswordHasher
{
    public const string RehashedPasswordHash = "rehashed-password-hash";

    /// <summary>The result <see cref="Verify"/> returns; the stored hash itself is never interpreted.</summary>
    public PasswordVerificationStatus VerifyResult { get; set; } = PasswordVerificationStatus.Success;

    public int VerifyCallCount { get; private set; }

    public int DummyVerificationCallCount { get; private set; }

    public int HashCallCount { get; private set; }

    public string? LastVerifiedPassword { get; private set; }

    public string? LastVerifiedPasswordHash { get; private set; }

    public string? LastDummyVerificationPassword { get; private set; }

    public string Hash(string password)
    {
        HashCallCount++;

        return RehashedPasswordHash;
    }

    public PasswordVerificationStatus Verify(string password, string passwordHash)
    {
        VerifyCallCount++;
        LastVerifiedPassword = password;
        LastVerifiedPasswordHash = passwordHash;

        return VerifyResult;
    }

    public void PerformDummyVerification(string password)
    {
        DummyVerificationCallCount++;
        LastDummyVerificationPassword = password;
    }
}
