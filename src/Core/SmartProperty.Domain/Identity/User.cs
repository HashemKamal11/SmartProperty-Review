namespace SmartProperty.Domain.Identity;

public sealed class User
{
    private User()
    {
        Email = string.Empty;
        FirstName = string.Empty;
        LastName = string.Empty;
    }

    public User(
        Guid id,
        string email,
        string firstName,
        string lastName,
        DateTimeOffset createdAt)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException("User id must not be empty.", nameof(id));
        }

        Id = id;
        Email = NormalizeEmail(email);
        FirstName = TrimRequired(firstName, nameof(firstName));
        LastName = TrimRequired(lastName, nameof(lastName));
        Status = UserStatus.Pending;
        CreatedAt = createdAt;
        UpdatedAt = createdAt;
    }

    public Guid Id { get; private set; }
    public string Email { get; private set; }
    public string FirstName { get; private set; }
    public string LastName { get; private set; }
    public UserStatus Status { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }

    public void Activate(DateTimeOffset updatedAt)
    {
        ValidateUpdatedAt(updatedAt);

        Status = UserStatus.Active;
        UpdatedAt = updatedAt;
    }

    public void Suspend(DateTimeOffset updatedAt)
    {
        ValidateUpdatedAt(updatedAt);

        Status = UserStatus.Suspended;
        UpdatedAt = updatedAt;
    }

    public void Deactivate(DateTimeOffset updatedAt)
    {
        ValidateUpdatedAt(updatedAt);

        Status = UserStatus.Deactivated;
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

    private static string NormalizeEmail(string email)
    {
        return TrimRequired(email, nameof(email)).ToLowerInvariant();
    }

    private static string TrimRequired(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("Value must not be empty.", parameterName);
        }

        return value.Trim();
    }
}
