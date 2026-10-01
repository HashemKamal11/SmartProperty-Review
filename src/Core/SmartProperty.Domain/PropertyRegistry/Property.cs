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

    public Property(Guid id, PropertyType type, PropertyAddress address, DateTimeOffset createdAt)
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

        Id = id;
        Type = type;
        Address = address;
        Status = PropertyStatus.Active;
        CreatedAt = createdAt;
        UpdatedAt = createdAt;
    }

    public Guid Id { get; private set; }
    public PropertyType Type { get; private set; }
    public PropertyAddress Address { get; private set; }
    public PropertyStatus Status { get; private set; }
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
