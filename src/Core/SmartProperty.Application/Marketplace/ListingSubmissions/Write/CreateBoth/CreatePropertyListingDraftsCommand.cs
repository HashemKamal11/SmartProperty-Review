using SmartProperty.Application.Abstractions.Messaging;

namespace SmartProperty.Application.Marketplace.ListingSubmissions.Write.CreateBoth;

/// <summary>
/// Creates the draft listings that advertise one existing property for Sale, for Rent, or for both.
/// </summary>
/// <remarks>
/// Each present side is a full draft with its own commercial terms. There is no "Both" purpose: advertising for both
/// is two purpose-specific drafts created together, and each needs its own valid grant.
/// </remarks>
public sealed record CreatePropertyListingDraftsCommand(
    Guid PropertyId,
    ListingDraftContentInput? Sale,
    ListingDraftContentInput? Rent) : ICommand<PropertyListingDraftsResult>;

/// <summary>The drafts that were created; the side that was not requested is null.</summary>
public sealed record PropertyListingDraftsResult(ListingDraftResult? Sale, ListingDraftResult? Rent);
