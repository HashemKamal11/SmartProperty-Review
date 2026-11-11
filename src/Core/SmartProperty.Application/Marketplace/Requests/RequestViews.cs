namespace SmartProperty.Application.Marketplace.Requests;

public enum RequestKind
{
    Purchase = 1,
    Rental = 2
}

/// <summary>The states a request can be filtered by. The names are the stored statuses.</summary>
public enum RequestStatusFilter
{
    Submitted = 1,
    Accepted = 2,
    Rejected = 3,
    WithdrawnByRequester = 4,

    /// <summary>Closed without a decision about the request itself: the property was committed elsewhere or the advertisement closed.</summary>
    ClosedListingUnavailable = 5
}

/// <summary>Which side of a commitment the caller is on.</summary>
public enum CommitmentRole
{
    Customer = 1,
    Owner = 2
}

/// <summary>
/// One request, as the person allowed to see it sees it. Purchase and rental requests share this shape; the fields
/// that only a rental has are null for a purchase.
/// </summary>
/// <remarks>
/// <see cref="Amount"/> is the offer for a purchase and the price per pricing period for a rental, copied from the
/// approved listing when the request was made. <see cref="RequesterName"/> is filled only for the advertiser's view.
/// A request from before dated rentals has no dates and no price.
/// </remarks>
public sealed record MarketplaceRequestView(
    RequestKind Kind,
    Guid RequestId,
    Guid ListingId,
    string Status,
    DateTimeOffset SubmittedAt,
    DateTimeOffset? DecidedAt,
    decimal? Amount,
    string? Currency,
    string? PricingPeriod,
    DateOnly? StartDate,
    DateOnly? EndDate,
    TimeOnly? CheckInTime,
    TimeOnly? CheckOutTime,
    string? TimeZoneId,
    string? Message,
    string? RequesterName);

public sealed record RequestPage(IReadOnlyList<MarketplaceRequestView> Items, long TotalCount);

/// <summary>
/// One commitment, as its customer or its owner sees it. An unsigned commitment past its deadline is shown as
/// Expired even before anything has stored that, since nothing depends on a timer having run.
/// </summary>
public sealed record CommitmentView(
    Guid CommitmentId,
    string Kind,
    string Status,
    Guid ListingId,
    decimal Amount,
    string Currency,
    string? PricingPeriod,
    DateOnly? StartDate,
    DateOnly? EndDate,
    TimeOnly? CheckInTime,
    TimeOnly? CheckOutTime,
    string? TimeZoneId,
    DateTimeOffset AcceptedAt,
    DateTimeOffset SigningDeadline,
    DateTimeOffset? SignedAt,
    DateTimeOffset? HandedOverAt,
    DateTimeOffset? CompletedAt,
    DateTimeOffset? ExpiredAt,
    bool IsOwner);

public sealed record CommitmentPage(IReadOnlyList<CommitmentView> Items, long TotalCount);
