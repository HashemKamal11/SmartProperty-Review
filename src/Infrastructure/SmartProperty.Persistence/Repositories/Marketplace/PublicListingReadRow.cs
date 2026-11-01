namespace SmartProperty.Persistence.Repositories.Marketplace;

// Unmapped SQL projection: contains public values only, never private snapshot JSON or join identities.
internal sealed class PublicListingReadRow
{
    public Guid ListingId { get; set; }
    public int Purpose { get; set; }
    public string Title { get; set; } = null!;
    public string Description { get; set; } = null!;
    public decimal Amount { get; set; }
    public string Currency { get; set; } = null!;
    public int? RentPeriod { get; set; }
    public int PropertyType { get; set; }
    public string Country { get; set; } = null!;
    public string Region { get; set; } = null!;
    public string City { get; set; } = null!;
    public string District { get; set; } = null!;
    public DateTimeOffset PublishedAt { get; set; }
}
