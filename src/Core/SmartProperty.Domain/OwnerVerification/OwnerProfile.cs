namespace SmartProperty.Domain.OwnerVerification;

/// <summary>
/// The identity and national-address evidence a user must provide, and have approved, before they may add properties.
/// One profile per user.
/// </summary>
/// <remarks>
/// The account data of the first screen (name, email, phone) is not stored here: it is read from the user. The
/// status moves Draft, UnderReview, then Approved or Rejected. A Rejected profile, or an Approved one the user edits,
/// returns to Draft so it must be submitted again. An UnderReview profile cannot be edited.
/// </remarks>
public sealed class OwnerProfile
{
    public const int BuildingNumberLength = 4;
    public const int PostalCodeLength = 5;
    public const int AdditionalNumberLength = 4;
    public const int MaxTextLength = 200;
    public const int MaxRejectionReasonLength = 1000;
    public const int MinimumAgeYears = 18;

    private OwnerProfile()
    {
    }

    public OwnerProfile(Guid id, Guid userId, DateTimeOffset createdAt)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException("Owner profile id must not be empty.", nameof(id));
        }

        if (userId == Guid.Empty)
        {
            throw new ArgumentException("User id must not be empty.", nameof(userId));
        }

        Id = id;
        UserId = userId;
        Status = OwnerVerificationStatus.Draft;
        CreatedAt = createdAt;
        UpdatedAt = createdAt;
    }

    public Guid Id { get; private set; }
    public Guid UserId { get; private set; }
    public OwnerVerificationStatus Status { get; private set; }

    /// <summary>The country that issued the ID, one of <see cref="NationalIdRules.SupportedCountries"/>.</summary>
    public string? IdCountryCode { get; private set; }

    /// <summary>Digits only; unique per <see cref="IdCountryCode"/>.</summary>
    public string? NationalId { get; private set; }
    public string? Nationality { get; private set; }
    public DateOnly? BirthDate { get; private set; }
    public DateOnly? IdExpiryDate { get; private set; }
    public DateTimeOffset? IdentityConfirmedAt { get; private set; }

    public string? BuildingNumber { get; private set; }
    public string? StreetName { get; private set; }
    public string? District { get; private set; }
    public string? City { get; private set; }
    public string? PostalCode { get; private set; }
    public string? AdditionalNumber { get; private set; }
    public DateTimeOffset? AddressConfirmedAt { get; private set; }

    public string? AddressProofKey { get; private set; }
    public string? AddressProofContentType { get; private set; }
    public string? AddressProofFileName { get; private set; }

    public DateTimeOffset? SubmittedAt { get; private set; }
    public DateTimeOffset? ReviewedAt { get; private set; }
    public Guid? ReviewedByUserId { get; private set; }
    public string? RejectionReason { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }

    public bool IsIdentityComplete => NationalId is not null && IdentityConfirmedAt is not null;

    public bool IsAddressComplete =>
        BuildingNumber is not null && AddressConfirmedAt is not null && AddressProofKey is not null;

    /// <summary>True when the profile is waiting for a reviewer and so cannot be edited.</summary>
    public bool IsLockedForEditing => Status == OwnerVerificationStatus.UnderReview;

    /// <summary>
    /// Saves the national-ID step. The confirmation is the user's statement that the data matches the original ID.
    /// </summary>
    /// <returns>True when an approved profile was sent back to Draft by the edit, so its grant must be withdrawn.</returns>
    /// <remarks>
    /// <paramref name="nationalId"/> may contain spaces or hyphens; they are removed. The format depends on
    /// <paramref name="idCountryCode"/> (see <see cref="NationalIdRules"/>).
    /// </remarks>
    public bool SaveIdentity(
        string idCountryCode,
        string nationalId,
        string nationality,
        DateOnly birthDate,
        DateOnly idExpiryDate,
        bool confirmed,
        DateOnly today,
        DateTimeOffset at)
    {
        EnsureEditable();

        var country = NationalIdRules.NormalizeCountry(idCountryCode)
            ?? throw new ArgumentException(
                $"The ID country must be one of {string.Join(", ", NationalIdRules.SupportedCountries)}.",
                nameof(idCountryCode));

        var normalizedId = NationalIdRules.NormalizeNationalId(nationalId);

        if (NationalIdRules.Validate(country, normalizedId, birthDate) is { } idError)
        {
            throw new ArgumentException(idError, nameof(nationalId));
        }

        var trimmedNationality = TrimRequired(nationality, nameof(nationality));

        if (birthDate.Year < 1900 || birthDate > today.AddYears(-MinimumAgeYears))
        {
            throw new ArgumentException($"The owner must be at least {MinimumAgeYears} years old.", nameof(birthDate));
        }

        if (idExpiryDate <= today)
        {
            throw new ArgumentException("The ID has expired.", nameof(idExpiryDate));
        }

        if (!confirmed)
        {
            throw new ArgumentException("The data must be confirmed as matching the original ID.", nameof(confirmed));
        }

        IdCountryCode = country;
        NationalId = normalizedId;
        Nationality = trimmedNationality;
        BirthDate = birthDate;
        IdExpiryDate = idExpiryDate;
        IdentityConfirmedAt = at;

        return Touch(at);
    }

    /// <summary>Saves the national-address step.</summary>
    /// <returns>True when an approved profile was sent back to Draft by the edit.</returns>
    public bool SaveAddress(
        string buildingNumber,
        string streetName,
        string district,
        string city,
        string postalCode,
        string additionalNumber,
        bool confirmed,
        DateTimeOffset at)
    {
        EnsureEditable();

        if (!IsDigits(buildingNumber, BuildingNumberLength))
        {
            throw new ArgumentException(
                $"The building number must be exactly {BuildingNumberLength} digits.",
                nameof(buildingNumber));
        }

        if (!IsDigits(postalCode, PostalCodeLength))
        {
            throw new ArgumentException($"The postal code must be exactly {PostalCodeLength} digits.", nameof(postalCode));
        }

        if (!IsDigits(additionalNumber, AdditionalNumberLength))
        {
            throw new ArgumentException(
                $"The additional number must be exactly {AdditionalNumberLength} digits.",
                nameof(additionalNumber));
        }

        var street = TrimRequired(streetName, nameof(streetName));
        var districtValue = TrimRequired(district, nameof(district));
        var cityValue = TrimRequired(city, nameof(city));

        if (!confirmed)
        {
            throw new ArgumentException(
                "The address must be confirmed as the current national address.",
                nameof(confirmed));
        }

        BuildingNumber = buildingNumber;
        StreetName = street;
        District = districtValue;
        City = cityValue;
        PostalCode = postalCode;
        AdditionalNumber = additionalNumber;
        AddressConfirmedAt = at;

        return Touch(at);
    }

    /// <summary>Records the stored address-proof image, replacing any earlier one.</summary>
    /// <returns>True when an approved profile was sent back to Draft by the edit.</returns>
    public bool SetAddressProof(string key, string contentType, string? fileName, DateTimeOffset at)
    {
        EnsureEditable();

        AddressProofKey = TrimRequired(key, nameof(key));
        AddressProofContentType = TrimRequired(contentType, nameof(contentType));
        AddressProofFileName = string.IsNullOrWhiteSpace(fileName) ? null : Truncate(fileName.Trim(), MaxTextLength);

        return Touch(at);
    }

    /// <summary>Sends the finished profile to the reviewers.</summary>
    public void Submit(DateTimeOffset at)
    {
        EnsureEditable();

        if (!IsIdentityComplete)
        {
            throw new InvalidOperationException("The national ID step is not complete.");
        }

        if (!IsAddressComplete)
        {
            throw new InvalidOperationException("The national address step, with its proof image, is not complete.");
        }

        Status = OwnerVerificationStatus.UnderReview;
        SubmittedAt = at;
        ReviewedAt = null;
        ReviewedByUserId = null;
        RejectionReason = null;
        UpdatedAt = at;
    }

    public void Approve(Guid reviewerUserId, DateTimeOffset at)
    {
        EnsureUnderReview(reviewerUserId);

        Status = OwnerVerificationStatus.Approved;
        ReviewedAt = at;
        ReviewedByUserId = reviewerUserId;
        RejectionReason = null;
        UpdatedAt = at;
    }

    public void Reject(Guid reviewerUserId, string reason, DateTimeOffset at)
    {
        EnsureUnderReview(reviewerUserId);

        if (string.IsNullOrWhiteSpace(reason))
        {
            throw new ArgumentException("A rejection reason is required.", nameof(reason));
        }

        var trimmed = reason.Trim();

        if (trimmed.Length > MaxRejectionReasonLength)
        {
            throw new ArgumentException(
                $"The reason must be at most {MaxRejectionReasonLength} characters.",
                nameof(reason));
        }

        Status = OwnerVerificationStatus.Rejected;
        ReviewedAt = at;
        ReviewedByUserId = reviewerUserId;
        RejectionReason = trimmed;
        UpdatedAt = at;
    }

    private static bool IsDigits(string? value, int length)
    {
        return value is not null && value.Length == length && value.All(char.IsAsciiDigit);
    }

    private void EnsureEditable()
    {
        if (IsLockedForEditing)
        {
            throw new InvalidOperationException("A profile under review cannot be changed.");
        }
    }

    private void EnsureUnderReview(Guid reviewerUserId)
    {
        if (reviewerUserId == Guid.Empty)
        {
            throw new ArgumentException("Reviewer id must not be empty.", nameof(reviewerUserId));
        }

        if (Status != OwnerVerificationStatus.UnderReview)
        {
            throw new InvalidOperationException("Only a profile under review can be decided.");
        }
    }

    /// <summary>An edit returns an approved or rejected profile to Draft; true when it was approved.</summary>
    private bool Touch(DateTimeOffset at)
    {
        var wasApproved = Status == OwnerVerificationStatus.Approved;

        if (Status != OwnerVerificationStatus.Draft)
        {
            Status = OwnerVerificationStatus.Draft;
            SubmittedAt = null;
            ReviewedAt = null;
            ReviewedByUserId = null;
            RejectionReason = null;
        }

        UpdatedAt = at;

        return wasApproved;
    }

    private static string TrimRequired(string? value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("Value must not be empty.", parameterName);
        }

        var trimmed = value.Trim();

        if (trimmed.Length > MaxTextLength)
        {
            throw new ArgumentException($"Value must be at most {MaxTextLength} characters.", parameterName);
        }

        return trimmed;
    }

    private static string Truncate(string value, int length) => value.Length <= length ? value : value[..length];
}
