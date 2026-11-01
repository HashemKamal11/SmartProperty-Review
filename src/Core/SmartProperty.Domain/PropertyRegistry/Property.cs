namespace SmartProperty.Domain.PropertyRegistry;

/// <summary>
/// Canonical real-estate asset identity. The registry is platform-global: a property does not belong to a
/// workspace, so functional modules reference this record rather than redefining a property of their own.
/// </summary>
public sealed class Property
{
    private Property()
    {
        // The address is populated by the persistence layer after construction.
        Address = null!;
    }

    public const int MaxRoomCount = 100;
    public static readonly DateOnly EarliestBuiltDate = new(1800, 1, 1);

    public Property(
        Guid id,
        PropertyType type,
        PropertyAddress address,
        DateTimeOffset createdAt,
        int? bedrooms = null,
        int? bathrooms = null,
        DateOnly? builtDate = null,
        FinishingStatus? finishingStatus = null)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException("Property id must not be empty.", nameof(id));
        }

        if (!Enum.IsDefined(type))
        {
            throw new ArgumentOutOfRangeException(nameof(type), type, "Property type is not supported.");
        }

        ArgumentNullException.ThrowIfNull(address);
        ValidateSpecifications(bedrooms, bathrooms, builtDate, finishingStatus);

        Id = id;
        Type = type;
        Address = address;
        Bedrooms = bedrooms;
        Bathrooms = bathrooms;
        BuiltDate = builtDate;
        FinishingStatus = finishingStatus;
        Status = PropertyStatus.Active;
        CreatedAt = createdAt;
        UpdatedAt = createdAt;
    }

    public Guid Id { get; private set; }
    public PropertyType Type { get; private set; }
    public PropertyAddress Address { get; private set; }
    public PropertyStatus Status { get; private set; }
    public int? Bedrooms { get; private set; }
    public int? Bathrooms { get; private set; }
    public DateOnly? BuiltDate { get; private set; }
    public FinishingStatus? FinishingStatus { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }

    /// <summary>
    /// Retires the property from active use. Archiving is the only removal the registry supports; a property
    /// record is never destroyed, because other modules reference it as the canonical asset.
    /// </summary>
    public void Archive(DateTimeOffset updatedAt)
    {
        if (Status != PropertyStatus.Active)
        {
            throw new InvalidOperationException("Only an active property can be archived.");
        }

        ValidateUpdatedAt(updatedAt);

        Status = PropertyStatus.Archived;
        UpdatedAt = updatedAt;
    }

    /// <summary>
    /// Corrects the physical form and location of an active property, replacing the address as a whole. Identity,
    /// lifecycle, and creation instant are never touched.
    /// </summary>
    /// <remarks>
    /// This overwrites the current state; it records no revision. An archived property is retired and cannot be
    /// corrected. Values identical to the current ones are not a change, so <see cref="UpdatedAt"/> keeps meaning
    /// "when the data last changed".
    /// </remarks>
    /// <returns>True when the type or address actually changed; false when the values were already current.</returns>
    public bool UpdateDetails(
        PropertyType type,
        PropertyAddress address,
        DateTimeOffset updatedAt,
        int? bedrooms = null,
        int? bathrooms = null,
        DateOnly? builtDate = null,
        FinishingStatus? finishingStatus = null)
    {
        if (Status != PropertyStatus.Active)
        {
            throw new InvalidOperationException("Only an active property can be updated.");
        }

        if (!Enum.IsDefined(type))
        {
            throw new ArgumentOutOfRangeException(nameof(type), type, "Property type is not supported.");
        }

        ArgumentNullException.ThrowIfNull(address);
        ValidateSpecifications(bedrooms, bathrooms, builtDate, finishingStatus);

        // A no-op is decided before the timestamp is examined: nothing changes, so the proposed instant is irrelevant
        // and a clock running behind the stored update must not turn an unchanged correction into a failure.
        if (Type == type
            && Address == address
            && Bedrooms == bedrooms
            && Bathrooms == bathrooms
            && BuiltDate == builtDate
            && FinishingStatus == finishingStatus)
        {
            return false;
        }

        ValidateUpdatedAt(updatedAt);

        Type = type;
        Address = address;
        Bedrooms = bedrooms;
        Bathrooms = bathrooms;
        BuiltDate = builtDate;
        FinishingStatus = finishingStatus;
        UpdatedAt = updatedAt;

        return true;
    }

    private static void ValidateSpecifications(
        int? bedrooms,
        int? bathrooms,
        DateOnly? builtDate,
        FinishingStatus? finishingStatus)
    {
        if (bedrooms is < 0 or > MaxRoomCount)
        {
            throw new ArgumentOutOfRangeException(
                nameof(bedrooms), bedrooms, $"Bedrooms must be between 0 and {MaxRoomCount}.");
        }

        if (bathrooms is < 0 or > MaxRoomCount)
        {
            throw new ArgumentOutOfRangeException(
                nameof(bathrooms), bathrooms, $"Bathrooms must be between 0 and {MaxRoomCount}.");
        }

        if (builtDate < EarliestBuiltDate)
        {
            throw new ArgumentOutOfRangeException(
                nameof(builtDate), builtDate, $"Built date must not be earlier than {EarliestBuiltDate:yyyy-MM-dd}.");
        }

        if (finishingStatus is { } status && !Enum.IsDefined(status))
        {
            throw new ArgumentOutOfRangeException(
                nameof(finishingStatus), finishingStatus, "Finishing status is not supported.");
        }
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
}
