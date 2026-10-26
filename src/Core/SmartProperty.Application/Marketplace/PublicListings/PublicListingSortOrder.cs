namespace SmartProperty.Application.Marketplace.PublicListings;

/// <summary>
/// How a public listing search is ordered. Every order ends with a deterministic tiebreaker so paging never repeats
/// or skips a listing.
/// </summary>
public enum PublicListingSortOrder
{
    /// <summary>Most recently published first. The default.</summary>
    Newest = 1,

    /// <summary>Lowest price first. Requires a currency, because amounts in different currencies do not compare.</summary>
    PriceAsc = 2,

    /// <summary>Highest price first. Requires a currency, because amounts in different currencies do not compare.</summary>
    PriceDesc = 3
}
