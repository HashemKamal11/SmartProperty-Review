namespace SmartProperty.Persistence.Repositories.Marketplace;

// Unmapped SQL projection: contains public values only, never private snapshot JSON or join identities. The pricing
// options, the rental terms and the blocked dates are the listing's public parts, taken member by member.
internal sealed class PublicListingReadRow
{
    public Guid ListingId { get; set; }

    /// <summary>Used only to group a property's listings; it is never copied into a public read model.</summary>
    public Guid PropertyId { get; set; }
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

    /// <summary>The price of the pricing period the search asked for, or null when none was asked for or it is not offered.</summary>
    public decimal? PeriodAmount { get; set; }

    public string? PeriodCurrency { get; set; }

    /// <summary>The listing's pricing options as JSON, or null for a listing stated with one price.</summary>
    public string? PricingOptionsJson { get; set; }

    /// <summary>The listing's rental terms as JSON, or null when it has none.</summary>
    public string? RentalTermsJson { get; set; }

    /// <summary>Dates a rental commitment holds, as <c>start|end,start|end</c>; null when nothing is held.</summary>
    public string? BlockedRangesText { get; set; }
}
