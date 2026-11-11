using SmartProperty.Application.Abstractions.Messaging;
using SmartProperty.Domain.Marketplace;

namespace SmartProperty.Application.Marketplace.Requests.Create;

/// <summary>
/// A customer's offer to buy. The customer is the authenticated user; the price offered defaults to the listing's
/// price and, when given, is in the listing's own currency.
/// </summary>
public sealed record CreatePurchaseRequestCommand(Guid ListingId, decimal? OfferedAmount, string? Message)
    : ICommand<RequestCreatedResult>;

/// <summary>
/// A customer's request to rent for dates. The price is not part of it: it is copied from the chosen pricing option
/// of the approved listing.
/// </summary>
public sealed record CreateRentalRequestCommand(
    Guid ListingId,
    RentPeriod PricingPeriod,
    DateOnly StartDate,
    DateOnly EndDate,
    string? Message) : ICommand<RequestCreatedResult>;

public sealed record RequestCreatedResult(RequestKind Kind, Guid RequestId, Guid ListingId, string Status, DateTimeOffset SubmittedAt);
