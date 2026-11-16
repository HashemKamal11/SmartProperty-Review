using SmartProperty.Application.Abstractions.Ai;
using SmartProperty.Application.Abstractions.Identity;
using SmartProperty.Application.Abstractions.Messaging;
using SmartProperty.Application.Abstractions.Persistence.Repositories;
using SmartProperty.Application.Abstractions.Time;
using SmartProperty.Application.Marketplace.Authority;
using SmartProperty.Application.Marketplace.ListingSubmissions.Write;
using SmartProperty.Common.Results;
using SmartProperty.Domain.Marketplace;

namespace SmartProperty.Application.Marketplace.Requests.Decide;

/// <summary>
/// The advertiser accepts an open request: the property becomes committed, its current advertisements close, and the
/// other open requests are closed with their history kept.
/// </summary>
/// <remarks>
/// <para>
/// <b>Order.</b> Account, then the advertiser is established from the request's approved version, then the locks in
/// the fixed order: property, the advertiser's grants (both purposes, shared), the Sale listing, the Rent listing, the
/// property's open requests, the property's open commitments. Every decision is made from those reads.
/// </para>
/// <para>
/// <b>Verified advertising authority.</b> Accepting needs the advertiser's grant for the request's purpose to be in
/// force at this moment, and the request to have been made against the version the listing publishes now. A listing
/// that was edited and approved again since the request is a request to renew, not to accept.
/// </para>
/// <para>
/// <b>What accepting does.</b> The winning request becomes a commitment with a fixed signing deadline (the
/// administrator's window, read now and stored, so a later change never moves it). Both of the property's published
/// advertisements are closed, so the owner advertises again only by an explicit new submission and review. Every other
/// open request, of either kind, is closed as unavailable. Commitments already in force are never touched: an approved
/// booking or a signed agreement is not cancelled by a later acceptance.
/// </para>
/// <para>
/// <b>Rules between commitments.</b> A rental needs its dates free of any commitment in force and no sale in force. A
/// sale needs no sale in force and no daily or weekly rental in force (a short stay is an occupancy that blocks selling);
/// a monthly or yearly rental is preserved and does not stop a sale. An unsigned commitment past its deadline is expired
/// here, inside this transaction, and counts for nothing; nothing relies on a timer. The database refuses overlapping
/// dates and a second active sale as a last line, so two simultaneous acceptances cannot both win.
/// </para>
/// </remarks>
public sealed class AcceptRequestCommandHandler(
    ICurrentUser currentUser,
    IUserRepository users,
    IMarketplaceRequestWriteRepository repository,
    IMarketplaceSettingsRepository settings,
    IDateTimeProvider clock,
    IListingSearchIndexer? indexer = null)
    : ICommandHandler<AcceptRequestCommand, AcceptRequestResult>
{
    public async Task<Result<AcceptRequestResult>> Handle(
        AcceptRequestCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        var acting = await ActingWriter.ResolveAsync(currentUser, users, cancellationToken);

        if (acting.Error is { } accountError)
        {
            return Result<AcceptRequestResult>.Failure(accountError);
        }

        var located = await ReceivedRequest.LocateAsync(repository, acting.UserId, command.Kind, command.RequestId, cancellationToken);

        if (located is null)
        {
            return Result<AcceptRequestResult>.Failure(RequestErrors.RequestNotFound);
        }

        var purpose = command.Kind == RequestKind.Rental ? ListingPurpose.Rent : ListingPurpose.Sale;
        var windowHours = await settings.GetSigningWindowHoursAsync(cancellationToken);
        var now = clock.UtcNow;

        // ---- locks, in the one fixed order
        var property = await repository.LockPropertyAsync(located.PropertyId, cancellationToken);

        if (property is null)
        {
            return Result<AcceptRequestResult>.Failure(RequestErrors.RequestNotFound);
        }

        ListingEligibility? eligibility = null;

        foreach (var each in new[] { ListingPurpose.Sale, ListingPurpose.Rent })
        {
            var grant = await repository.GetGrantForSharedLockAsync(acting.UserId, located.PropertyId, each, cancellationToken);

            if (each == purpose)
            {
                eligibility = GrantEligibilityEvaluator.Evaluate(grant, acting.UserId, located.PropertyId, each, now);
            }
        }

        var saleListing = await repository.GetListingAsync(located.PropertyId, ListingPurpose.Sale, cancellationToken);
        var rentListing = await repository.GetListingAsync(located.PropertyId, ListingPurpose.Rent, cancellationToken);
        var openPurchases = await repository.GetOpenPurchaseRequestsAsync(located.PropertyId, cancellationToken);
        var openRentals = await repository.GetOpenRentalRequestsAsync(located.PropertyId, cancellationToken);
        var openCommitments = await repository.GetOpenCommitmentsAsync(located.PropertyId, cancellationToken);

        // ---- decisions, from the reads above
        if (eligibility is null || !eligibility.IsEligible)
        {
            return Result<AcceptRequestResult>.Failure(WriteErrors.NotEligible);
        }

        var winningListing = purpose == ListingPurpose.Rent ? rentListing : saleListing;

        if (winningListing is null || winningListing.Id != located.ListingId)
        {
            return Result<AcceptRequestResult>.Failure(RequestErrors.RequestNotFound);
        }

        if (winningListing.PublicationStatus != ListingPublicationStatus.Published
            || winningListing.AvailabilityStatus != ListingAvailabilityStatus.Open
            || !property.IsActive
            || property.HasMarketplaceReservation
            || property.AwaitingOwnershipTransfer)
        {
            return Result<AcceptRequestResult>.Failure(RequestErrors.NotOpen);
        }

        if (winningListing.PublishedSubmissionId != located.SubmissionId)
        {
            return Result<AcceptRequestResult>.Failure(RequestErrors.RequestOutdated);
        }

        // An unsigned commitment past its deadline stops counting, and is recorded as expired in this same commit.
        foreach (var commitment in openCommitments)
        {
            commitment.Expire(now);
        }

        var active = openCommitments.Where(commitment => commitment.IsActiveAt(now)).ToArray();
        // The advertisements this acceptance is about to close, remembered before the Domain changes them, so the search
        // index can be told once the commit is done.
        var closing = new[] { saleListing, rentListing }
            .Where(candidate => candidate is { PublicationStatus: ListingPublicationStatus.Published })
            .Select(candidate => candidate!.Id)
            .ToArray();

        var deadline = now.AddHours(windowHours);
        PropertyCommitment accepted;
        AcceptRequestResult result;

        try
        {
            if (command.Kind == RequestKind.Rental)
            {
                var winner = openRentals.FirstOrDefault(request => request.Id == command.RequestId);

                if (winner is null)
                {
                    return Result<AcceptRequestResult>.Failure(RequestErrors.NotOpen);
                }

                if (active.Any(commitment => commitment.Kind == CommitmentKind.Sale))
                {
                    return Result<AcceptRequestResult>.Failure(RequestErrors.PropertyCommitted);
                }

                if (winner is not { IsDated: true, StartDate: { } start, EndDate: { } end })
                {
                    return Result<AcceptRequestResult>.Failure(RequestErrors.RentalTermsMissing);
                }

                if (active.Any(commitment => commitment.Overlaps(start, end)))
                {
                    return Result<AcceptRequestResult>.Failure(RequestErrors.DatesUnavailable);
                }

                accepted = CommitmentAcceptance.AcceptRental(
                    winningListing,
                    saleListing,
                    winner,
                    openRentals.Where(request => request.Id != winner.Id).ToArray(),
                    openPurchases,
                    acting.UserId,
                    now,
                    deadline);
            }
            else
            {
                var winner = openPurchases.FirstOrDefault(request => request.Id == command.RequestId);

                if (winner is null)
                {
                    return Result<AcceptRequestResult>.Failure(RequestErrors.NotOpen);
                }

                if (active.Any(commitment => commitment.Kind == CommitmentKind.Sale))
                {
                    return Result<AcceptRequestResult>.Failure(RequestErrors.PropertyCommitted);
                }

                // A daily or weekly rental in force is an occupancy: the property cannot be sold until it ends. A
                // monthly or yearly rental is preserved by a sale and does not stop it.
                if (active.Any(commitment => commitment.IsShortStay))
                {
                    return Result<AcceptRequestResult>.Failure(RequestErrors.ShortStayOccupancy);
                }

                accepted = CommitmentAcceptance.AcceptSale(
                    winningListing,
                    rentListing,
                    winner,
                    openPurchases.Where(request => request.Id != winner.Id).ToArray(),
                    openRentals,
                    acting.UserId,
                    now,
                    deadline);
            }

            result = new AcceptRequestResult(command.Kind, command.RequestId, accepted.Id, "Accepted", now, deadline);
        }
        catch (Exception exception) when (exception is InvalidOperationException or ArgumentException)
        {
            // The Domain validates the whole transition before it changes anything, so nothing is half applied.
            return Result<AcceptRequestResult>.Failure(RequestErrors.NotOpen);
        }

        await repository.AddCommitmentAsync(accepted, cancellationToken);

        if (await RequestCommit.SaveAsync(repository, cancellationToken) is { } conflict)
        {
            return Result<AcceptRequestResult>.Failure(conflict);
        }

        // The commit above is the decision. Telling the search index is best effort and never changes it.
        if (indexer is not null)
        {
            foreach (var listingId in closing)
            {
                await indexer.RemoveAsync(listingId, cancellationToken);
            }
        }

        return Result<AcceptRequestResult>.Success(result);
    }
}
