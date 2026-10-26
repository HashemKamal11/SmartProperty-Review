namespace SmartProperty.Application.Marketplace.PublicListings;

/// <summary>One page of public search results and the total number of matching listings.</summary>
public sealed record PublicListingPage(
    IReadOnlyList<PublicListingSummary> Items,
    long TotalCount);
