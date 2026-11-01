namespace SmartProperty.Application.Marketplace.ListingSubmissions.Write.Discard;

/// <summary>
/// The outcome of a discard: whether the listing slot itself was released because it was empty and never published.
/// </summary>
public sealed record DiscardListingDraftResult(bool ListingRemoved);
