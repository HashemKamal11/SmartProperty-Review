namespace SmartProperty.Domain.Identity;

/// <summary>
/// One six-digit code issued to a user for one <see cref="OneTimeCodePurpose"/>. Stores only a hash of the code,
/// never the code itself. A code is usable until it expires, is consumed, or reaches
/// <see cref="MaxFailedAttempts"/> wrong guesses.
/// </summary>
public sealed class OneTimeCode
{
    public const int MaxFailedAttempts = 5;

    private OneTimeCode()
    {
        CodeHash = string.Empty;
    }

    public OneTimeCode(
        Guid id,
        Guid userId,
        OneTimeCodePurpose purpose,
        string codeHash,
        DateTimeOffset createdAt,
        DateTimeOffset expiresAt)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException("One-time code id must not be empty.", nameof(id));
        }

        if (userId == Guid.Empty)
        {
            throw new ArgumentException("User id must not be empty.", nameof(userId));
        }

        if (!Enum.IsDefined(purpose))
        {
            throw new ArgumentException("One-time code purpose is not recognized.", nameof(purpose));
        }

        if (string.IsNullOrWhiteSpace(codeHash))
        {
            throw new ArgumentException("Code hash must not be empty.", nameof(codeHash));
        }

        if (expiresAt <= createdAt)
        {
            throw new ArgumentException("Expiration date must be later than the created date.", nameof(expiresAt));
        }

        Id = id;
        UserId = userId;
        Purpose = purpose;
        CodeHash = codeHash;
        CreatedAt = createdAt;
        ExpiresAt = expiresAt;
    }

    public Guid Id { get; private set; }
    public Guid UserId { get; private set; }
    public OneTimeCodePurpose Purpose { get; private set; }
    public string CodeHash { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset ExpiresAt { get; private set; }
    public int FailedAttempts { get; private set; }
    public DateTimeOffset? ConsumedAt { get; private set; }

    public bool IsActive(DateTimeOffset now)
    {
        return ConsumedAt is null && now < ExpiresAt && FailedAttempts < MaxFailedAttempts;
    }

    public void RegisterFailedAttempt()
    {
        FailedAttempts++;
    }

    public void Consume(DateTimeOffset consumedAt)
    {
        if (ConsumedAt is not null)
        {
            throw new InvalidOperationException("One-time code is already consumed.");
        }

        if (consumedAt < CreatedAt)
        {
            throw new ArgumentException("Consumed date cannot be earlier than the created date.", nameof(consumedAt));
        }

        ConsumedAt = consumedAt;
    }
}
