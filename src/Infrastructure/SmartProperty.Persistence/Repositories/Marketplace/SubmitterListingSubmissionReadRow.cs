namespace SmartProperty.Persistence.Repositories.Marketplace;

// Unmapped scalar allowlists. Neither projection carries identity/authority/evidence snapshots.
// Separate types prevent descriptions and decision reasons from being materialized by a list read.
internal sealed class SubmitterListingSubmissionReadRow
{
    public Guid SubmissionId { get; set; }
    public Guid ListingId { get; set; }
    public string PurposeText { get; set; } = null!;
    public int Version { get; set; }
    public string StatusText { get; set; } = null!;
    public string Title { get; set; } = null!;
    public decimal Amount { get; set; }
    public string Currency { get; set; } = null!;
    public int? RentPeriod { get; set; }
    public bool? Negotiable { get; set; }
    public int PropertyType { get; set; }
    public string Country { get; set; } = null!;
    public string Region { get; set; } = null!;
    public string City { get; set; } = null!;
    public string District { get; set; } = null!;
    public int? Representation { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? SubmittedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public bool IsCurrentlyPublished { get; set; }
    public string? PricingOptionsJson { get; set; }
    public string? RentalTermsJson { get; set; }
}

internal sealed class SubmitterListingSubmissionDetailReadRow
{
    public Guid SubmissionId { get; set; }
    public Guid ListingId { get; set; }
    public string PurposeText { get; set; } = null!;
    public int Version { get; set; }
    public string StatusText { get; set; } = null!;
    public string Title { get; set; } = null!;
    public decimal Amount { get; set; }
    public string Currency { get; set; } = null!;
    public int? RentPeriod { get; set; }
    public bool? Negotiable { get; set; }
    public int PropertyType { get; set; }
    public string Country { get; set; } = null!;
    public string Region { get; set; } = null!;
    public string City { get; set; } = null!;
    public string District { get; set; } = null!;
    public int? Representation { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? SubmittedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public bool IsCurrentlyPublished { get; set; }
    public string Description { get; set; } = null!;
    public string? PricingOptionsJson { get; set; }
    public string? RentalTermsJson { get; set; }
    public bool? DecisionApproved { get; set; }
    public string? DecisionReason { get; set; }
    public DateTimeOffset? DecidedAt { get; set; }

    public SubmitterListingSubmissionReadRow Summary() => new()
    {
        SubmissionId = SubmissionId,
        ListingId = ListingId,
        PurposeText = PurposeText,
        Version = Version,
        StatusText = StatusText,
        Title = Title,
        Amount = Amount,
        Currency = Currency,
        RentPeriod = RentPeriod,
        Negotiable = Negotiable,
        PropertyType = PropertyType,
        Country = Country,
        Region = Region,
        City = City,
        District = District,
        Representation = Representation,
        CreatedAt = CreatedAt,
        SubmittedAt = SubmittedAt,
        UpdatedAt = UpdatedAt,
        IsCurrentlyPublished = IsCurrentlyPublished,
        PricingOptionsJson = PricingOptionsJson,
        RentalTermsJson = RentalTermsJson,
    };
}
