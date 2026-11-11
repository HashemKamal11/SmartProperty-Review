using SmartProperty.Application.Abstractions.Authorization;
using SmartProperty.Application.Abstractions.Identity;
using SmartProperty.Application.Abstractions.Marketplace;
using SmartProperty.Application.Abstractions.Messaging;
using SmartProperty.Application.Abstractions.Persistence.Repositories;
using SmartProperty.Application.Abstractions.Time;
using SmartProperty.Application.Marketplace.ListingSubmissions.Write;
using SmartProperty.Common.Results;
using SmartProperty.Domain.Marketplace;

namespace SmartProperty.Application.Marketplace.ListingSubmissions.Review.Decide;

/// <summary>
/// Rejects a pending submission with a reason. Nothing is published and the current publication, if any, is untouched.
/// </summary>
/// <remarks>
/// A rejection depends on neither the submitter's grant nor the evidence, so it is never blocked by an unavailable
/// provider; it still needs the decide permission and an independent reviewer. The reason is stored for the submitter
/// and is never returned to reviewers by any administrative read.
/// </remarks>
public sealed class RejectListingSubmissionCommandHandler(
    ICurrentUser currentUser,
    IUserRepository users,
    IReviewDecisionAuthorizer authorizer,
    IReviewerIndependenceProvider independence,
    IListingSubmissionWriteRepository repository,
    IDateTimeProvider clock)
    : ICommandHandler<RejectListingSubmissionCommand, ReviewDecisionResult>
{
    public const int MaxReasonLength = 2000;

    public async Task<Result<ReviewDecisionResult>> Handle(
        RejectListingSubmissionCommand command,
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

        var reason = command.Reason?.Trim();

        if (string.IsNullOrEmpty(reason) || reason.Length > MaxReasonLength)
        {
            return Result<ReviewDecisionResult>.Failure(ListingSubmissionErrors.ValidationFailed(
                $"A rejection reason of 1 to {MaxReasonLength} characters is required."));
        }

        var now = clock.UtcNow;
        var locked = await ReviewDecisionContext.LockAsync(repository, command.SubmissionId, now, cancellationToken);

        if (locked.IsFailure)
        {
            return Result<ReviewDecisionResult>.Failure(locked.Error!);
        }

        var submission = locked.Value.Submission;

        if (submission.Status != ListingSubmissionStatus.PendingReview || submission.ReviewDecision is not null)
        {
            return Result<ReviewDecisionResult>.Failure(ReviewErrors.NotPending);
        }

        var context = await ReviewDecisionContext.ReviewerAsync(independence, submission, reviewer.UserId, cancellationToken);

        if (context.IsFailure)
        {
            return Result<ReviewDecisionResult>.Failure(context.Error!);
        }

        try
        {
            submission.Reject(Guid.NewGuid(), context.Value, reason, now);
        }
        catch (InvalidOperationException)
        {
            return Result<ReviewDecisionResult>.Failure(ReviewErrors.NotPending);
        }

        if (await WriteCommit.SaveAsync(repository, WriteCommit.ForChange, cancellationToken) is { } conflict)
        {
            return Result<ReviewDecisionResult>.Failure(conflict);
        }

        return Result<ReviewDecisionResult>.Success(
            new ReviewDecisionResult(submission.Id, locked.Value.Listing.Id, submission.Status, now));
    }
}
