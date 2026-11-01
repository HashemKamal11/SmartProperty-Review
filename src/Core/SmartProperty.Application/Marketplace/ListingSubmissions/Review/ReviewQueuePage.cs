namespace SmartProperty.Application.Marketplace.ListingSubmissions.Review;

/// <summary>One page of the review queue and the total number of matches.</summary>
public sealed record ReviewQueuePage(
    IReadOnlyList<ReviewQueueItem> Items,
    long TotalCount);
