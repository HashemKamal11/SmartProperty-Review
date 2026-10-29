namespace SmartProperty.Domain.Identity;

/// <summary>
/// One phone verification code issued to a user. Stores only a hash of the code, never the code itself.
/// A code is usable until it expires, is consumed, or reaches <see cref="MaxFailedAttempts"/> wrong guesses.
/// </summary>
public sealed class PhoneVerification
{
    public const int MaxFailedAttempts = 5;

    private PhoneVerification()
    {
        CodeHash = string.Empty;
    }

    public PhoneVerification(
        Guid id,
        Guid userId,
        string codeHash,
        DateTimeOffset createdAt,
        DateTimeOffset expiresAt)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException("Phone verification id must not be empty.", nameof(id));
        }

        if (userId == Guid.Empty)
        {
            throw new ArgumentException("User id must not be empty.", nameof(userId));
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
        CodeHash = codeHash;
        CreatedAt = createdAt;
        ExpiresAt = expiresAt;
    }

    public Guid Id { get; private set; }
    public Guid UserId { get; private set; }
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
            throw new InvalidOperationException("Phone verification is already consumed.");
        }

        if (consumedAt < CreatedAt)
        {
            throw new ArgumentException("Consumed date cannot be earlier than the created date.", nameof(consumedAt));
        }

        ConsumedAt = consumedAt;
    }
}
