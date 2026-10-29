using SmartProperty.Application.Abstractions.Persistence;
using SmartProperty.Application.Abstractions.Persistence.Repositories;
using SmartProperty.Common.Results;
using SmartProperty.Domain.Identity;

namespace SmartProperty.Application.Authentication.OneTimeCodes;

internal enum CodeCheck
{
    Confirmed,
    Invalid,
    TooManyAttempts
}

/// <summary>A freshly issued code. The plain code exists only here, long enough to be sent.</summary>
internal sealed record IssuedCode(string Code, DateTimeOffset ExpiresAt);

/// <summary>
/// The issue and check steps every one-time-code flow shares, so the phone, email, and password reset flows cannot
/// drift apart on expiry, attempt limits, or cooldown.
/// </summary>
internal static class OneTimeCodeFlow
{
    /// <summary>
    /// Checks a submitted code against the newest code for the user and purpose. A wrong guess is counted and saved
    /// here. On <see cref="CodeCheck.Confirmed"/> the code is consumed but not yet saved: the caller applies its own
    /// change and saves everything together.
    /// </summary>
    public static async Task<CodeCheck> CheckAsync(
        IOneTimeCodeRepository codes,
        IUnitOfWork unitOfWork,
        Guid userId,
        OneTimeCodePurpose purpose,
        string submittedCode,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        // A malformed code cannot match, so it is refused without spending one of the code's attempts.
        if (!OneTimeCodePolicy.IsWellFormedCode(submittedCode))
        {
            return CodeCheck.Invalid;
        }

        var code = await codes.GetLatestAsync(userId, purpose, cancellationToken);

        if (code is null || code.ConsumedAt is not null || now >= code.ExpiresAt)
        {
            return CodeCheck.Invalid;
        }

        if (!code.IsActive(now))
        {
            return CodeCheck.TooManyAttempts;
        }

        if (!OneTimeCodePolicy.CodeMatches(userId, purpose, submittedCode, code.CodeHash))
        {
            code.RegisterFailedAttempt();

            if (!await TrySaveAsync(unitOfWork, cancellationToken))
            {
                // A concurrent guess counted first; this one is refused like any other wrong code.
                return CodeCheck.Invalid;
            }

            return code.IsActive(now) ? CodeCheck.Invalid : CodeCheck.TooManyAttempts;
        }

        code.Consume(now);

        return CodeCheck.Confirmed;
    }

    /// <summary>
    /// Retires the unused codes of the user for the purpose and adds a new one, unsaved. With
    /// <paramref name="enforceCooldown"/> a new code cannot be requested within
    /// <see cref="OneTimeCodePolicy.ResendCooldown"/> of the previous one.
    /// </summary>
    public static async Task<Result<IssuedCode>> IssueAsync(
        IOneTimeCodeRepository codes,
        Guid userId,
        OneTimeCodePurpose purpose,
        DateTimeOffset now,
        bool enforceCooldown,
        CancellationToken cancellationToken)
    {
        if (enforceCooldown)
        {
            var latest = await codes.GetLatestAsync(userId, purpose, cancellationToken);

            if (latest is not null && now < latest.CreatedAt.Add(OneTimeCodePolicy.ResendCooldown))
            {
                return Result<IssuedCode>.Failure(OneTimeCodeErrors.ResendTooSoon(purpose));
            }
        }

        foreach (var previous in await codes.GetUnconsumedAsync(userId, purpose, cancellationToken))
        {
            previous.Consume(now);
        }

        var plainCode = OneTimeCodePolicy.GenerateCode();
        var issued = OneTimeCodePolicy.Issue(userId, purpose, plainCode, now);

        await codes.AddAsync(issued, cancellationToken);

        return Result<IssuedCode>.Success(new IssuedCode(plainCode, issued.ExpiresAt));
    }

    /// <summary>
    /// Saves, reporting false when another writer changed the same code first (two parallel guesses, or a guess
    /// racing a verification) instead of letting a free attempt through.
    /// </summary>
    public static async Task<bool> TrySaveAsync(IUnitOfWork unitOfWork, CancellationToken cancellationToken)
    {
        try
        {
            await unitOfWork.SaveChangesAsync(cancellationToken);

            return true;
        }
        catch (ConcurrencyConflictException exception)
            when (exception.Resource == PersistenceResource.OneTimeCode)
        {
            return false;
        }
    }
}
