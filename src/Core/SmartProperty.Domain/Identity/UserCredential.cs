namespace SmartProperty.Domain.Identity;

/// <summary>
/// Authentication secret state for a user, kept separate from the user's identity profile.
/// Stores only the encoded password hash; plaintext passwords are never accepted or stored.
/// </summary>
public sealed class UserCredential
{
    private UserCredential()
    {
        PasswordHash = string.Empty;
    }

    public UserCredential(Guid userId, string passwordHash, DateTimeOffset createdAt)
    {
        if (userId == Guid.Empty)
        {
            throw new ArgumentException("User id must not be empty.", nameof(userId));
        }

        UserId = userId;
        PasswordHash = ValidatePasswordHash(passwordHash);
        CreatedAt = createdAt;
        UpdatedAt = createdAt;
    }

    public Guid UserId { get; private set; }
    public string PasswordHash { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }

    public void ChangePasswordHash(string passwordHash, DateTimeOffset updatedAt)
    {
        var validatedPasswordHash = ValidatePasswordHash(passwordHash);
        ValidateUpdatedAt(updatedAt);

        PasswordHash = validatedPasswordHash;
        UpdatedAt = updatedAt;
    }

    private void ValidateUpdatedAt(DateTimeOffset updatedAt)
    {
        if (updatedAt < UpdatedAt)
        {
            throw new ArgumentException(
                "Updated date cannot be earlier than the current updated date.",
                nameof(updatedAt));
        }
    }

    private static string ValidatePasswordHash(string passwordHash)
    {
        if (string.IsNullOrWhiteSpace(passwordHash))
        {
            throw new ArgumentException("Password hash must not be empty.", nameof(passwordHash));
        }

        return passwordHash;
    }
}
