using SmartProperty.Application.Abstractions.Ai;
using SmartProperty.Application.Abstractions.Authorization;
using SmartProperty.Application.Abstractions.Identity;
using SmartProperty.Application.Abstractions.Marketplace;
using SmartProperty.Application.Abstractions.Messaging;
using SmartProperty.Application.Abstractions.Persistence.Repositories;
using SmartProperty.Application.Abstractions.Time;
using SmartProperty.Application.Marketplace.ListingSubmissions.Write;
using SmartProperty.Application.Marketplace.Requests;
using SmartProperty.Common.Results;
using SmartProperty.Domain.Marketplace;

namespace SmartProperty.Application.Marketplace.ListingSubmissions.Review.Decide;

/// <summary>
/// Approves a pending submission and publishes it, after establishing under the locks that it still deserves to be.
/// </summary>
/// <remarks>
/// <para>
/// <b>Order.</b> The reviewer's account and decide permission first, then the locks in the fixed order (property,
/// the submitter's grant, listing, submissions). Everything below is decided from the reads made under those locks.
/// </para>
/// <para>
/// <b>What must still hold at the moment of approval.</b> The submission is awaiting review; the submitter's grant is
/// in force and still describes the authority the submission carries; the property is active and unreserved; the
/// submitted content still matches the registry; every piece of evidence has exactly one current Clean assessment from
/// the trusted provider; and the reviewer is independent. A provider that cannot answer is a clear 503, never a pass.
/// </para>
/// <para>
/// The Domain's <c>ApproveAndPublish</c> performs the transition, replacing the previous publication (which becomes
/// Superseded) in the same commit. Rental occupancy and purchase restrictions are added by the commitment rules.
/// </para>
/// </remarks>
public sealed class ApproveListingSubmissionCommandHandler(
    ICurrentUser currentUser,
    IUserRepository users,
    IReviewDecisionAuthorizer authorizer,
    IReviewerIndependenceProvider independence,
    IMarketplaceRequestWriteRepository repository,
    IEvidenceAssessmentProvider evidence,
    IDateTimeProvider clock,
    IListingSearchIndexer? indexer = null)
    : ICommandHandler<ApproveListingSubmissionCommand, ReviewDecisionResult>
{
    public async Task<Result<ReviewDecisionResult>> Handle(
        ApproveListingSubmissionCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        var reviewer = await ReviewerGate.ResolveAsync(currentUser, users, authorizer, cancellationToken);

        if (reviewer.Error is { } gateError)
        {
            return Result<ReviewDecisionResult>.Failure(gateError);
        }

        if (command.SubmissionId == Guid.Empty)
        {
            return Result<ReviewDecisionResult>.Failure(ListingSubmissionErrors.NotFound);
        }

        var now = clock.UtcNow;
        var locked = await ReviewDecisionContext.LockAsync(repository, command.SubmissionId, now, cancellationToken);

        if (locked.IsFailure)
        {
            return Result<ReviewDecisionResult>.Failure(locked.Error!);
        }

        var (submission, listing, current, property, eligibility) = locked.Value;

        if (submission.Status != ListingSubmissionStatus.PendingReview || submission.ReviewDecision is not null)
        {
            return Result<ReviewDecisionResult>.Failure(ReviewErrors.NotPending);
        }

        var context = await ReviewDecisionContext.ReviewerAsync(independence, submission, reviewer.UserId, cancellationToken);

        if (context.IsFailure)
        {
            return Result<ReviewDecisionResult>.Failure(context.Error!);
        }

        if (!eligibility.IsEligible
            || !property.IsActive
            || property.HasMarketplaceReservation
            || property.AwaitingOwnershipTransfer
            || !DraftAuthority.IsStillValid(submission.Authority, eligibility.Authority!, now)
            || !CanonicalListingContent.Matches(submission.Content, property))
        {
            return Result<ReviewDecisionResult>.Failure(ReviewErrors.CannotPublish);
        }

        // What the property is already committed to limits what may be advertised. Commitments are read under the
        // property lock, and one whose signature is overdue no longer counts. A sale cannot be advertised while a daily
        // or weekly rental is in force or a sale is, and a rental cannot be advertised while a sale is in force. A
        // monthly or yearly rental in force does not stop a sale from being advertised: the sale preserves it.
        var open = await repository.GetOpenCommitmentsAsync(submission.PropertyId, cancellationToken);
        var active = open.Where(commitment => commitment.IsActiveAt(now)).ToArray();

        if (active.Any(commitment => commitment.Kind == CommitmentKind.Sale))
        {
            return Result<ReviewDecisionResult>.Failure(ReviewErrors.PropertyCommitted);
        }

        if (submission.Purpose == ListingPurpose.Sale && active.Any(commitment => commitment.IsShortStay))
        {
            return Result<ReviewDecisionResult>.Failure(ReviewErrors.ShortStayOccupancy);
        }

        var verification = await SubmissionEvidenceVerifier.VerifyAsync(submission, evidence, now, cancellationToken);

        if (verification.Result.IsFailure)
        {
            return Result<ReviewDecisionResult>.Failure(verification.Result.Error!);
        }

        var facts = eligibility.SubmissionFacts!;

        try
        {
            submission.ApproveAndPublish(
                Guid.NewGuid(),
                listing,
                current,
                context.Value,
                now,
                propertyAvailable: true,
                verification.Result.Value,
                facts.LegalAuthorityVerified,
                facts.WorkspaceOperationAuthorized,
                facts.ActingUserIsDeclaredOwnerVerified);
        }
        catch (InvalidOperationException)
        {
            // The Domain validates the whole transition before it changes anything, so nothing is half applied.
            return Result<ReviewDecisionResult>.Failure(ReviewErrors.CannotPublish);
        }

        if (await WriteCommit.SaveAsync(repository, WriteCommit.ForChange, cancellationToken) is { } conflict)
        {
            return Result<ReviewDecisionResult>.Failure(conflict);
        }

        // The commit above is the decision. Telling the search index is best effort and never changes it.
        if (indexer is not null)
        {
            await indexer.IndexAsync(submission.PropertyId, submission.Purpose, cancellationToken);
        }

        return Result<ReviewDecisionResult>.Success(
            new ReviewDecisionResult(submission.Id, listing.Id, submission.Status, now));
    }
}
