using SmartProperty.Domain.Locations;

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
        Physical = PropertyPhysicalInfo.Empty;
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
        FinishingStatus? finishingStatus = null,
        IEnumerable<Amenity>? amenities = null,
        IEnumerable<PropertyView>? views = null,
        PropertyPhysicalInfo? physical = null,
        Area? area = null,
        Compound? compound = null,
        Guid? ownerUserId = null)
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
        ValidatePlace(area, compound);

        if (ownerUserId == Guid.Empty)
        {
            throw new ArgumentException("Owner user id must not be empty.", nameof(ownerUserId));
        }

        Id = id;
        OwnerUserId = ownerUserId;
        Type = type;
        Address = address;
        Bedrooms = bedrooms;
        Bathrooms = bathrooms;
        BuiltDate = builtDate;
        FinishingStatus = finishingStatus;
        _amenities.AddRange(DistinctById(amenities, amenity => amenity.Id));
        _views.AddRange(DistinctById(views, view => view.Id));
        Physical = physical ?? PropertyPhysicalInfo.Empty;
        SetPlace(area, compound);
        Status = PropertyStatus.Active;
        CreatedAt = createdAt;
        UpdatedAt = createdAt;
    }

    public Guid Id { get; private set; }

    /// <summary>The user who registered the property, or null for one registered before owners were recorded.</summary>
    public Guid? OwnerUserId { get; private set; }

    public PropertyType Type { get; private set; }
    public PropertyAddress Address { get; private set; }
    public PropertyStatus Status { get; private set; }
    public int? Bedrooms { get; private set; }
    public int? Bathrooms { get; private set; }
    public DateOnly? BuiltDate { get; private set; }
    public FinishingStatus? FinishingStatus { get; private set; }

    private readonly List<Amenity> _amenities = [];
    private readonly List<PropertyView> _views = [];
    private readonly List<PropertyImage> _images = [];

    /// <summary>The amenities this property offers, with no duplicates.</summary>
    public IReadOnlyCollection<Amenity> Amenities => _amenities;

    /// <summary>What the property looks out on, with no duplicates.</summary>
    public IReadOnlyCollection<PropertyView> Views => _views;

    /// <summary>
    /// The photos of the property. Read-only here: images are added, removed, and reordered by their own use cases,
    /// not by correcting the property's details, so a details update never touches them.
    /// </summary>
    public IReadOnlyCollection<PropertyImage> Images => _images;

    /// <summary>
    /// The physical facts that have no field of their own: area, floor, and whether it is furnished. Commercial and
    /// advertisement data lives in the Marketplace listing, never here.
    /// </summary>
    public PropertyPhysicalInfo Physical { get; private set; }

    /// <summary>The area the property is in, or null while its place is not chosen. City and country come from it.</summary>
    public Guid? AreaId { get; private set; }

    public Area? Area { get; private set; }

    /// <summary>The compound inside <see cref="Area"/>, when the property is in one.</summary>
    public Guid? CompoundId { get; private set; }

    public Compound? Compound { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }

    /// <summary>
    /// Registers the property to its new owner after an approved ownership transfer. The property keeps its identity and
    /// its whole history; only who controls it changes.
    /// </summary>
    public void TransferOwnership(Guid newOwnerUserId, DateTimeOffset updatedAt)
    {
        if (newOwnerUserId == Guid.Empty)
        {
            throw new ArgumentException("The new owner is required.", nameof(newOwnerUserId));
        }

        if (Status != PropertyStatus.Active)
        {
            throw new InvalidOperationException("Only an active property changes owner.");
        }

        if (OwnerUserId == newOwnerUserId)
        {
            throw new InvalidOperationException("The property is already registered to this owner.");
        }

        ValidateUpdatedAt(updatedAt);

        OwnerUserId = newOwnerUserId;
        UpdatedAt = updatedAt;
    }

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
        FinishingStatus? finishingStatus = null,
        IEnumerable<Amenity>? amenities = null,
        IEnumerable<PropertyView>? views = null,
        PropertyPhysicalInfo? physical = null,
        Area? area = null,
        Compound? compound = null)
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
        ValidatePlace(area, compound);

        // A no-op is decided before the timestamp is examined: nothing changes, so the proposed instant is irrelevant
        // and a clock running behind the stored update must not turn an unchanged correction into a failure.
        if (Type == type
            && Address == address
            && Bedrooms == bedrooms
            && Bathrooms == bathrooms
            && BuiltDate == builtDate
            && FinishingStatus == finishingStatus
            && HasSameIds(_amenities, amenities, amenity => amenity.Id)
            && HasSameIds(_views, views, view => view.Id)
            && Physical == (physical ?? PropertyPhysicalInfo.Empty)
            && AreaId == area?.Id
            && CompoundId == compound?.Id)
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
        ReplaceItems(_amenities, amenities, amenity => amenity.Id);
        ReplaceItems(_views, views, view => view.Id);
        Physical = physical ?? PropertyPhysicalInfo.Empty;
        SetPlace(area, compound);
        UpdatedAt = updatedAt;

        return true;
    }

    private static void ValidatePlace(Area? area, Compound? compound)
    {
        if (compound is not null && area is null)
        {
            throw new ArgumentException("A compound can only be chosen together with its area.", nameof(compound));
        }

        if (compound is not null && area is not null && compound.AreaId != area.Id)
        {
            throw new ArgumentException("The compound does not belong to the area.", nameof(compound));
        }
    }

    private void SetPlace(Area? area, Compound? compound)
    {
        Area = area;
        AreaId = area?.Id;
        Compound = compound;
        CompoundId = compound?.Id;
    }

    private static List<T> DistinctById<T>(IEnumerable<T>? items, Func<T, Guid> id)
    {
        return (items ?? []).DistinctBy(id).ToList();
    }

    private static bool HasSameIds<T>(List<T> current, IEnumerable<T>? incoming, Func<T, Guid> id)
    {
        return DistinctById(incoming, id).Select(id).ToHashSet().SetEquals(current.Select(id));
    }

    // Keeps the items already attached, so only the real difference reaches the database.
    private static void ReplaceItems<T>(List<T> current, IEnumerable<T>? incoming, Func<T, Guid> id)
    {
        var wanted = DistinctById(incoming, id);

        current.RemoveAll(existing => wanted.TrueForAll(item => id(item) != id(existing)));

        foreach (var item in wanted)
        {
            if (current.TrueForAll(existing => id(existing) != id(item)))
            {
                current.Add(item);
            }
        }
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
