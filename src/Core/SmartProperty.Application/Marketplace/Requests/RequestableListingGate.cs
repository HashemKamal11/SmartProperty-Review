using SmartProperty.Application.Abstractions.Persistence.Repositories;
using SmartProperty.Application.Marketplace.Authority;
using SmartProperty.Common.Results;
using SmartProperty.Domain.Marketplace;

namespace SmartProperty.Application.Marketplace.Requests;

/// <summary>A listing that is open to requests right now, with everything a request is judged against, read under the locks.</summary>
internal sealed record RequestableListing(
    PropertyListing Listing,
    ListingSubmission Published,
    LockedPropertyView Property,
    IReadOnlyList<PropertyCommitment> ActiveCommitments);

/// <summary>
/// The one place a customer's request starts: it locks the property, checks that the advertiser still holds a valid
/// grant for the purpose, and confirms the listing is published, open and the version the caller is asking about.
/// </summary>
/// <remarks>
/// <para>
/// <b>Order.</b> The listing is first read only to learn which property to lock and who authored the published
/// version. After the property lock, the advertiser's grant is read shared and evaluated, then the listing row is
/// locked and read again; every decision uses the read made under the locks.
/// </para>
/// <para>
/// <b>Verified advertising authority.</b> A published listing whose advertiser has since lost the grant stops taking
/// requests: it is the same not-found as an unpublished one, so a customer learns nothing about why.
/// </para>
/// </remarks>
internal static class RequestableListingGate
{
    public static async Task<Result<RequestableListing>> OpenAsync(
        IMarketplaceRequestWriteRepository repository,
        Guid customerUserId,
        Guid listingId,
        ListingPurpose purpose,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var known = await repository.FindListingAsync(listingId, cancellationToken);

        if (known is null || known.Purpose != purpose || known.PublishedSubmissionId is not { } publishedId)
        {
            return Result<RequestableListing>.Failure(RequestErrors.ListingUnavailable);
        }

        var preRead = await repository.GetSubmissionAsync(publishedId, cancellationToken);

        if (preRead is null)
        {
            return Result<RequestableListing>.Failure(RequestErrors.ListingUnavailable);
        }

        var advertiser = preRead.Authority.ActingUserId;
        var property = await repository.LockPropertyAsync(known.PropertyId, cancellationToken);

        if (property is null || property.PropertyId != known.PropertyId)
        {
            return Result<RequestableListing>.Failure(RequestErrors.ListingUnavailable);
        }

        var grant = await repository.GetGrantForSharedLockAsync(advertiser, known.PropertyId, purpose, cancellationToken);
        var eligibility = GrantEligibilityEvaluator.Evaluate(grant, advertiser, known.PropertyId, purpose, now);
        var listing = await repository.GetListingByIdAsync(listingId, cancellationToken);

        if (!eligibility.IsEligible
            || listing is null
            || listing.PublishedSubmissionId != publishedId
            || listing.PublicationStatus != ListingPublicationStatus.Published
            || listing.AvailabilityStatus != ListingAvailabilityStatus.Open
            || !property.IsActive
            || property.HasMarketplaceReservation
            || property.AwaitingOwnershipTransfer)
        {
            return Result<RequestableListing>.Failure(RequestErrors.ListingUnavailable);
        }

        // An approved version is immutable, so the earlier read is the version the listing publishes.
        if (customerUserId == advertiser || customerUserId == preRead.Authority.DeclaredOwner.LinkedUserId)
        {
            return Result<RequestableListing>.Failure(RequestErrors.OwnListing);
        }

        var open = await repository.GetOpenCommitmentsAsync(known.PropertyId, cancellationToken);

        return Result<RequestableListing>.Success(
            new RequestableListing(listing, preRead, property, open.Where(commitment => commitment.IsActiveAt(now)).ToArray()));
    }
}
