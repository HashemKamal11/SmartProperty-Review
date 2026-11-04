namespace SmartProperty.Application.Abstractions.Authentication;

/// <summary>
/// Limits repeated failed sign-ins per account, so a password cannot be guessed at the speed of the network.
/// </summary>
/// <remarks>
/// Keyed by the normalized email whether or not an account uses it, so being locked out reveals nothing about which
/// emails are registered. State is per process; a deployment with several API instances multiplies the allowance by
/// the instance count, and the per-IP rate limiter at the API edge is the second, independent layer.
/// </remarks>
public interface ILoginAttemptLimiter
{
    /// <summary>True while further attempts for this email are refused.</summary>
    bool IsLockedOut(string email);

    /// <summary>Counts one failed attempt, locking the email once the allowance is spent.</summary>
    void RegisterFailure(string email);

    /// <summary>Forgets the failures of an email after a successful sign-in.</summary>
    void Reset(string email);
}
