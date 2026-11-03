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
        DateTimeOffset createdAt,
        string? phoneNumber = null)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException("User id must not be empty.", nameof(id));
        }

        Id = id;
        Email = NormalizeEmail(email);
        FirstName = TrimRequired(firstName, nameof(firstName));
        LastName = TrimRequired(lastName, nameof(lastName));
        PhoneNumber = phoneNumber is null ? null : PhoneNumberFormat.Normalize(phoneNumber);
        Status = UserStatus.Pending;
        CreatedAt = createdAt;
        UpdatedAt = createdAt;
    }

    public Guid Id { get; private set; }
    public string Email { get; private set; }
    public string FirstName { get; private set; }
    public string LastName { get; private set; }
    /// <summary>
    /// E.164 phone number. Null only for accounts created before phone numbers were collected.
    /// </summary>
    public string? PhoneNumber { get; private set; }
    public DateTimeOffset? PhoneVerifiedAt { get; private set; }
    public bool IsPhoneVerified => PhoneVerifiedAt is not null;
    public DateTimeOffset? EmailVerifiedAt { get; private set; }
    public bool IsEmailVerified => EmailVerifiedAt is not null;

    /// <summary>
    /// True when the account has proved every contact it registered with: the phone number and the email. Accounts
    /// that predate phone numbers have none and are treated as complete, so they are not held back.
    /// </summary>
    public bool IsContactVerificationComplete => PhoneNumber is null || (IsPhoneVerified && IsEmailVerified);
    public UserStatus Status { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }

    public void VerifyPhone(DateTimeOffset verifiedAt)
    {
        if (PhoneNumber is null)
        {
            throw new InvalidOperationException("User has no phone number to verify.");
        }

        ValidateUpdatedAt(verifiedAt);

        PhoneVerifiedAt = verifiedAt;
        UpdatedAt = verifiedAt;
    }

    public void VerifyEmail(DateTimeOffset verifiedAt)
    {
        ValidateUpdatedAt(verifiedAt);

        EmailVerifiedAt = verifiedAt;
        UpdatedAt = verifiedAt;
    }

    public void Activate(DateTimeOffset updatedAt)
    {
        ValidateUpdatedAt(updatedAt);

        Status = UserStatus.Active;
        UpdatedAt = updatedAt;
    }

    /// <summary>
    /// Activates a Pending account once it has proved every contact it registered with, so no administrator has to
    /// activate it. Any other status (Active, Suspended, Deactivated) is left alone, and so is a Pending account that
    /// still has an unverified contact. Returns whether the account was activated.
    /// </summary>
    public bool ActivateWhenContactVerified(DateTimeOffset updatedAt)
    {
        if (Status != UserStatus.Pending || !IsContactVerificationComplete)
        {
            return false;
        }

        Activate(updatedAt);

        return true;
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
