namespace SmartProperty.Domain.Identity;

/// <summary>
/// Persisted refresh token record. Stores only a hash of the opaque token; the raw token value is never stored.
/// </summary>
public sealed class RefreshToken
{
    private RefreshToken()
    {
        TokenHash = string.Empty;
    }

    public RefreshToken(
        Guid id,
        Guid userId,
        string tokenHash,
        DateTimeOffset createdAt,
        DateTimeOffset expiresAt)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException("Refresh token id must not be empty.", nameof(id));
        }

        if (userId == Guid.Empty)
        {
            throw new ArgumentException("User id must not be empty.", nameof(userId));
        }

        if (string.IsNullOrWhiteSpace(tokenHash))
        {
            throw new ArgumentException("Token hash must not be empty.", nameof(tokenHash));
        }

        if (expiresAt <= createdAt)
        {
            throw new ArgumentException("Expiration date must be later than the created date.", nameof(expiresAt));
        }

        Id = id;
        UserId = userId;
        TokenHash = tokenHash;
        CreatedAt = createdAt;
        ExpiresAt = expiresAt;
    }

    public Guid Id { get; private set; }
    public Guid UserId { get; private set; }
    public string TokenHash { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset ExpiresAt { get; private set; }
    public DateTimeOffset? RevokedAt { get; private set; }

    public bool IsActive(DateTimeOffset now)
    {
        return RevokedAt is null && now < ExpiresAt;
    }

    public void Revoke(DateTimeOffset revokedAt)
    {
        if (RevokedAt is not null)
        {
            throw new InvalidOperationException("Refresh token is already revoked.");
        }

        if (revokedAt < CreatedAt)
        {
            throw new ArgumentException("Revoked date cannot be earlier than the created date.", nameof(revokedAt));
        }

        RevokedAt = revokedAt;
    }
}
