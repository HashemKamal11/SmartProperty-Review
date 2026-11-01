using SmartProperty.Application.Abstractions.Persistence.Repositories;
using SmartProperty.Common.Results;
using SmartProperty.Domain.Marketplace;

namespace SmartProperty.Application.Marketplace.ListingSubmissions.Write;

/// <summary>A draft read under the property and grant locks, with its listing and the gate that opened it.</summary>
internal sealed record LockedDraft(ListingSubmission Draft, PropertyListing Listing, ListingWriteGateResult Gate);

/// <summary>
/// Finds the acting user's own draft and takes it through the locking contract.
/// </summary>
/// <remarks>
/// <para>
/// The lock order is property, then grant, then listing, then submissions. A submission names its property only once
/// it is read, so the draft is first read <b>only to learn which property and purpose to lock</b>. Nothing is decided
/// from that read. After the gate has evaluated the grant under the locks, the listing and the draft are read again,
/// and only that second read is used for every decision and every change.
/// </para>
/// <para>
/// A draft that is missing, belongs to someone else, or no longer agrees with its listing is the same not-found.
/// </para>
/// </remarks>
internal static class OwnDraft
{
    public static Task<ListingSubmission?> FindAsync(
        IListingSubmissionWriteRepository repository,
        Guid actingUserId,
        Guid submissionId,
        CancellationToken cancellationToken = default)
    {
        return repository.GetOwnSubmissionAsync(submissionId, actingUserId, cancellationToken);
    }

    /// <param name="allowUnavailableProperty">
    /// True only for discarding: a caller who is still eligible may remove their own draft even when the property has
    /// since been archived or reserved, since removal needs nothing from the property.
    /// </param>
    public static async Task<Result<LockedDraft>> LockAsync(
        IListingSubmissionWriteRepository repository,
        Guid actingUserId,
        ListingSubmission located,
        DateTimeOffset at,
        bool allowUnavailableProperty,
        CancellationToken cancellationToken = default)
    {
        var gate = await ListingWriteGate.EvaluateAsync(
            repository, actingUserId, located.PropertyId, located.Purpose, at, cancellationToken);

        if (!gate.IsOpen && !(allowUnavailableProperty && ReferenceEquals(gate.Error, WriteErrors.PropertyUnavailable)))
        {
            return Result<LockedDraft>.Failure(gate.Error!);
        }

        var listing = await repository.GetListingAsync(located.PropertyId, located.Purpose, cancellationToken);
        var draft = await repository.GetOwnSubmissionAsync(located.Id, actingUserId, cancellationToken);

        if (listing is null
            || draft is null
            || draft.ListingId != listing.Id
            || draft.PropertyId != located.PropertyId
            || draft.Purpose != located.Purpose)
        {
            return Result<LockedDraft>.Failure(ListingSubmissionErrors.NotFound);
        }

        if (!allowUnavailableProperty && IsClosedByReservation(listing))
        {
            return Result<LockedDraft>.Failure(WriteErrors.PropertyUnavailable);
        }

        return Result<LockedDraft>.Success(new LockedDraft(draft, listing, gate));
    }

    /// <summary>
    /// The draft is still the revision the client saw and has never been submitted. Anything else was changed by
    /// another request, and the only thing said is that.
    /// </summary>
    public static Error? EnsureUnchanged(ListingSubmission draft, int expectedRevision)
    {
        return draft.Revision != expectedRevision || draft.Status != ListingSubmissionStatus.Draft
            ? WriteErrors.StaleRevision
            : null;
    }

    public static bool IsClosedByReservation(PropertyListing listing)
    {
        return listing.ClosedByReservation || listing.AvailabilityStatus == ListingAvailabilityStatus.Reserved;
    }
}
