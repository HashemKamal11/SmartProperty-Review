using SmartProperty.Application.Abstractions.Marketplace;
using SmartProperty.Application.Abstractions.Persistence.Repositories;
using SmartProperty.Common.Results;

namespace SmartProperty.Application.Marketplace.Requests;

/// <summary>
/// The stable errors of the request and commitment workflow. None names a grant, a user, a price or a date of
/// someone else: a request, listing or commitment the caller may not see is the same not-found as a missing one.
/// </summary>
internal static class RequestErrors
{
    /// <summary>Missing, not published, closed, not open to requests, or its advertiser no longer holds the authority.</summary>
    public static readonly Error ListingUnavailable = new(
        "marketplace_requests.listing_unavailable",
        "The listing is not available for requests.",
        ErrorType.NotFound);

    public static readonly Error OwnListing = new(
        "marketplace_requests.own_listing",
        "You cannot make a request on a listing you advertise.",
        ErrorType.Forbidden);

    public static readonly Error RequestNotFound = new(
        "marketplace_requests.not_found",
        "The request was not found.",
        ErrorType.NotFound);

    public static readonly Error NotOpen = new(
        "marketplace_requests.not_open",
        "The request is no longer open.",
        ErrorType.Conflict);

    public static readonly Error AlreadyRequested = new(
        "marketplace_requests.already_requested",
        "You already have an open request on this listing.",
        ErrorType.Conflict);

    public static readonly Error DatesUnavailable = new(
        "marketplace_requests.dates_unavailable",
        "The property is already committed for some of these dates.",
        ErrorType.Conflict);

    public static readonly Error PropertyCommitted = new(
        "marketplace_requests.property_committed",
        "The property is committed to a sale.",
        ErrorType.Conflict);

    public static readonly Error ShortStayOccupancy = new(
        "marketplace_requests.short_stay_occupancy",
        "A daily or weekly rental is in force, so the property cannot be sold or advertised for sale until it ends.",
        ErrorType.Conflict);

    public static readonly Error RequestOutdated = new(
        "marketplace_requests.request_outdated",
        "The listing changed after this request was made. Ask the customer to request again.",
        ErrorType.Conflict);

    public static readonly Error RentalTermsMissing = new(
        "marketplace_requests.rental_terms_missing",
        "This rental has no check-in, check-out or availability terms, so dated requests cannot be made yet.",
        ErrorType.Conflict);

    public static readonly Error PricingOptionUnavailable = new(
        "marketplace_requests.pricing_option_unavailable",
        "The listing does not offer that pricing period.",
        ErrorType.Validation);

    public static readonly Error CommitmentNotFound = new(
        "marketplace_commitments.not_found",
        "The commitment was not found.",
        ErrorType.NotFound);

    public static readonly Error CommitmentStepNotAllowed = new(
        "marketplace_commitments.step_not_allowed",
        "The commitment cannot take that step now.",
        ErrorType.Conflict);

    public static readonly Error Stale = new(
        "marketplace_requests.concurrent_change",
        "The request was changed by another request. Reload and try again.",
        ErrorType.Conflict);

    public static Error Invalid(string description)
    {
        return new Error("validation.failed", description, ErrorType.Validation);
    }
}

/// <summary>The one commit of a request or commitment write, and the only place a lost race becomes a public error.</summary>
internal static class RequestCommit
{
    public static async Task<Error?> SaveAsync(
        IMarketplaceRequestWriteRepository repository,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await repository.SaveChangesAsync(cancellationToken);

            return null;
        }
        catch (ListingWriteConflictException exception)
        {
            return exception.Kind switch
            {
                ListingWriteConflictKind.RequestAlreadyOpen => RequestErrors.AlreadyRequested,
                ListingWriteConflictKind.DatesUnavailable => RequestErrors.DatesUnavailable,
                ListingWriteConflictKind.PropertyAlreadyCommitted => RequestErrors.PropertyCommitted,
                ListingWriteConflictKind.StaleRevision => RequestErrors.Stale,
                _ => throw new InvalidOperationException("An unexpected conflict kind reached a request write.", exception)
            };
        }
    }
}
