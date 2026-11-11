using SmartProperty.Application.Abstractions.Ai;
using SmartProperty.Application.Abstractions.Identity;
using SmartProperty.Application.Abstractions.Messaging;
using SmartProperty.Application.Abstractions.Persistence.Repositories;
using SmartProperty.Application.Abstractions.Time;
using SmartProperty.Common.Results;
using SmartProperty.Domain.Marketplace;

namespace SmartProperty.Application.Marketplace.ListingSubmissions.Write.Withdraw;

/// <summary>
/// Takes the caller's own published listing off the marketplace. Only that purpose is affected.
/// </summary>
/// <remarks>
/// <para>
/// The caller must be the user who authored the published version. A grant still in force is deliberately <b>not</b>
/// required: withdrawing only reduces exposure, so a lapsed or revoked grant must not trap an advertisement on the
/// marketplace. Anything else about the listing, including one that is not theirs, is the same not-found.
/// </para>
/// <para>
/// The listing's history, its published submission and every request stay as they are. Pending requests can no longer
/// be accepted because acceptance needs a published, open listing; the owner can still reject them. Advertising again
/// is a new draft that is submitted and reviewed afresh; the Domain refuses to republish a withdrawn listing from
/// anything but a version submitted after the withdrawal.
/// </para>
/// </remarks>
public sealed class WithdrawListingCommandHandler(
    ICurrentUser currentUser,
    IUserRepository users,
    IListingSubmissionWriteRepository repository,
    IDateTimeProvider clock,
    IListingSearchIndexer? indexer = null)
    : ICommandHandler<WithdrawListingCommand, WithdrawListingResult>
{
    public async Task<Result<WithdrawListingResult>> Handle(
        WithdrawListingCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        var acting = await ActingWriter.ResolveAsync(currentUser, users, cancellationToken);

        if (acting.Error is { } accountError)
        {
            return Result<WithdrawListingResult>.Failure(accountError);
        }

        var known = await repository.FindListingAsync(command.ListingId, cancellationToken);

        if (known is null)
        {
            return Result<WithdrawListingResult>.Failure(ListingSubmissionErrors.NotFound);
        }

        var property = await repository.LockPropertyAsync(known.PropertyId, cancellationToken);

        if (property is null || property.PropertyId != known.PropertyId)
        {
            return Result<WithdrawListingResult>.Failure(ListingSubmissionErrors.NotFound);
        }

        // Read only to take the locks in order; the grant does not decide a withdrawal.
        await repository.GetGrantForSharedLockAsync(acting.UserId, known.PropertyId, known.Purpose, cancellationToken);

        var listing = await repository.GetListingByIdAsync(command.ListingId, cancellationToken);

        if (listing is null || listing.PublishedSubmissionId is not { } publishedId)
        {
            return Result<WithdrawListingResult>.Failure(ListingSubmissionErrors.NotFound);
        }

        var published = await repository.GetSubmissionAsync(publishedId, cancellationToken);

        if (published is null || published.Authority.ActingUserId != acting.UserId)
        {
            return Result<WithdrawListingResult>.Failure(ListingSubmissionErrors.NotFound);
        }

        var now = clock.UtcNow;

        if (listing.PublicationStatus != ListingPublicationStatus.Published
            || listing.AvailabilityStatus != ListingAvailabilityStatus.Open)
        {
            return Result<WithdrawListingResult>.Failure(WriteErrors.ListingNotPublished);
        }

        try
        {
            listing.Withdraw(now);
        }
        catch (Exception exception) when (exception is InvalidOperationException or ArgumentException)
        {
            return Result<WithdrawListingResult>.Failure(WriteErrors.ListingNotPublished);
        }

        if (await WriteCommit.SaveAsync(repository, WriteCommit.ForChange, cancellationToken) is { } conflict)
        {
            return Result<WithdrawListingResult>.Failure(conflict);
        }

        // The commit above is the decision. Telling the search index is best effort and never changes it.
        if (indexer is not null)
        {
            await indexer.RemoveAsync(listing.Id, cancellationToken);
        }

        return Result<WithdrawListingResult>.Success(new WithdrawListingResult(listing.Id, now));
    }
}
