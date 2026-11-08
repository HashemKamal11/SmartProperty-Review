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
        Listing = PropertyListingInfo.Empty;
    }

    public const int MaxRoomCount = 100;
    public const int MaxAiSyncErrorLength = 1000;
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
        PropertyListingInfo? listing = null,
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
        Listing = listing ?? PropertyListingInfo.Empty;
        AiSyncStatus = AiSyncStatus.NotSynced;
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

    /// <summary>The market-facing facts: price, area, transaction type, presentation, and place ids.</summary>
    public PropertyListingInfo Listing { get; private set; }

    /// <summary>The area the property is in, or null while its place is not chosen. City and country come from it.</summary>
    public Guid? AreaId { get; private set; }

    public Area? Area { get; private set; }

    /// <summary>The compound inside <see cref="Area"/>, when the property is in one.</summary>
    public Guid? CompoundId { get; private set; }

    public Compound? Compound { get; private set; }

    /// <summary>Whether the AI search service holds this property. Bookkeeping: never changes <see cref="UpdatedAt"/>.</summary>
    public AiSyncStatus AiSyncStatus { get; private set; }

    public DateTimeOffset? AiSyncedAt { get; private set; }

    /// <summary>Why the property is not synced (missing data, or the failure of the last attempt), or null.</summary>
    public string? AiSyncError { get; private set; }

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
        FinishingStatus? finishingStatus = null,
        IEnumerable<Amenity>? amenities = null,
        IEnumerable<PropertyView>? views = null,
        PropertyListingInfo? listing = null,
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
            && Listing == (listing ?? PropertyListingInfo.Empty)
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
        Listing = listing ?? PropertyListingInfo.Empty;
        SetPlace(area, compound);
        UpdatedAt = updatedAt;

        return true;
    }

    /// <summary>
    /// The facts the AI search service requires that this property does not have yet, by their API names. Empty
    /// when the property can be sent.
    /// </summary>
    public IReadOnlyList<string> MissingForAiSync()
    {
        var missing = new List<string>();

        if (Listing.TransactionType is null) { missing.Add("transactionType"); }
        if (Listing.Price is null) { missing.Add("price"); }
        if (Listing.AreaSqm is null) { missing.Add("areaSqm"); }
        if (AreaId is null) { missing.Add("areaId"); }
        if (Bedrooms is null) { missing.Add("bedrooms"); }
        if (Bathrooms is null) { missing.Add("bathrooms"); }

        return missing;
    }

    /// <summary>Records that the AI service now holds the property as last saved.</summary>
    public void MarkAiSynced(DateTimeOffset at)
    {
        AiSyncStatus = AiSyncStatus.Synced;
        AiSyncedAt = at;
        AiSyncError = null;
    }

    /// <summary>Records that the AI service does not hold the property, with the reason when there is one.</summary>
    public void MarkAiNotSynced(string? reason = null)
    {
        AiSyncStatus = AiSyncStatus.NotSynced;
        AiSyncedAt = null;
        AiSyncError = TrimError(reason);
    }

    /// <summary>Records that the last attempt to send the property failed.</summary>
    public void MarkAiSyncFailed(string reason)
    {
        AiSyncStatus = AiSyncStatus.Failed;
        AiSyncError = TrimError(reason);
    }

    private static string? TrimError(string? reason)
    {
        if (string.IsNullOrWhiteSpace(reason))
        {
            return null;
        }

        var trimmed = reason.Trim();

        return trimmed.Length <= MaxAiSyncErrorLength ? trimmed : trimmed[..MaxAiSyncErrorLength];
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
