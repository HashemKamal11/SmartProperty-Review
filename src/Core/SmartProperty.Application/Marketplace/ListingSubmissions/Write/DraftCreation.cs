using SmartProperty.Application.Abstractions.Persistence.Repositories;
using SmartProperty.Common.Results;
using SmartProperty.Domain.Marketplace;

namespace SmartProperty.Application.Marketplace.ListingSubmissions.Write;

/// <summary>A draft built under the locks and not yet added to the unit of work.</summary>
internal sealed record PreparedDraft(PropertyListing Listing, bool IsNewListing, ListingSubmission Submission);

/// <summary>
/// The one place a new draft is built once the write gate is open: the canonical content, the listing slot, the
/// predecessor, and the submission. Nothing is added to the repository, so a refused request changes nothing.
/// </summary>
/// <remarks>
/// There is one listing per property and purpose. An open draft or a version awaiting review means the caller must
/// update or wait; only a reviewed latest version allows a successor. The published version is never touched: the draft
/// is a new submission, and publication changes only when a reviewer approves it.
/// </remarks>
internal static class DraftCreation
{
    public static async Task<Result<PreparedDraft>> PrepareAsync(
        IListingSubmissionWriteRepository repository,
        ListingWriteGateResult gate,
        ListingPurpose purpose,
        ListingDraftContentInput input,
        IReadOnlyList<ListingEvidenceReference> evidence,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var content = CanonicalListingContent.Build(gate.Property!, purpose, input);

        if (content.IsFailure)
        {
            return Result<PreparedDraft>.Failure(content.Error!);
        }

        var propertyId = gate.Property!.PropertyId;
        var listing = await repository.GetListingAsync(propertyId, purpose, cancellationToken);
        ListingSubmission? predecessor = null;

        if (listing is not null)
        {
            if (OwnDraft.IsClosedByReservation(listing))
            {
                return Result<PreparedDraft>.Failure(WriteErrors.PropertyUnavailable);
            }

            var latest = await repository.GetLatestSubmissionAsync(listing.Id, cancellationToken);

            if (latest is not null)
            {
                // Only a reviewed version has a successor. A draft or a version awaiting review is the open one.
                if (latest.Status is ListingSubmissionStatus.Draft or ListingSubmissionStatus.PendingReview)
                {
                    return Result<PreparedDraft>.Failure(WriteErrors.OpenRevisionExists);
                }

                predecessor = latest;
            }
        }

        var isNewListing = listing is null;
        listing ??= new PropertyListing(Guid.NewGuid(), propertyId, purpose, now);

        var submission = new ListingSubmission(
            Guid.NewGuid(),
            listing,
            content.Value,
            DraftAuthority.FromFacts(gate.Authority!),
            evidence,
            now,
            predecessor);

        return Result<PreparedDraft>.Success(new PreparedDraft(listing, isNewListing, submission));
    }
}
