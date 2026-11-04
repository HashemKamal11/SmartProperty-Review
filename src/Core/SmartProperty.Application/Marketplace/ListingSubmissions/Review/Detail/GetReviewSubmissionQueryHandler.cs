using SmartProperty.Application.Abstractions.Authorization;
using SmartProperty.Application.Abstractions.Identity;
using SmartProperty.Application.Abstractions.Messaging;
using SmartProperty.Application.Abstractions.Persistence.Repositories;
using SmartProperty.Common.Results;

namespace SmartProperty.Application.Marketplace.ListingSubmissions.Review.Detail;

public sealed class GetReviewSubmissionQueryHandler(
    ICurrentUser currentUser,
    IReviewReadAuthorizer authorizer,
    IReviewerListingSubmissionReadRepository submissions)
    : IQueryHandler<GetReviewSubmissionQuery, ReviewSubmissionDetail>
{
    public async Task<Result<ReviewSubmissionDetail>> Handle(
        GetReviewSubmissionQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        // Fails closed on an unusable subject instead of reading anything for anyone.
        if (currentUser.UserId is not { } reviewerUserId || reviewerUserId == Guid.Empty)
        {
            return Result<ReviewSubmissionDetail>.Failure(ListingSubmissionErrors.Unauthorized);
        }

        // Only an explicit true allows; an exception from the authorizer is never turned into access.
        if (!await authorizer.IsAuthorizedAsync(reviewerUserId, cancellationToken))
        {
            return Result<ReviewSubmissionDetail>.Failure(ReviewErrors.Forbidden);
        }

        // No submission can have the empty id, so asking the database would only confirm what is already known.
        if (query.SubmissionId == Guid.Empty)
        {
            return Result<ReviewSubmissionDetail>.Failure(ListingSubmissionErrors.NotFound);
        }

        // Reading does not depend on reviewer independence; that governs decisions only.
        var lookup = await submissions.GetForReviewAsync(query.SubmissionId, cancellationToken);

        ArgumentNullException.ThrowIfNull(lookup);

        switch (lookup.Outcome)
        {
            case ReviewDetailOutcome.NotFound:
                return Result<ReviewSubmissionDetail>.Failure(ListingSubmissionErrors.NotFound);

            case ReviewDetailOutcome.Found:
                // A repository that reports "found" without the detail, or with a different submission than the
                // one asked for, is a data-integrity fault. It is never shown and never turned into a refusal.
                if (lookup.Detail is null || lookup.Detail.SubmissionId != query.SubmissionId)
                {
                    throw new InvalidOperationException("The repository returned an inconsistent review lookup.");
                }

                return Result<ReviewSubmissionDetail>.Success(ForReviewer(lookup.Detail));

            default:
                // An outcome nobody defined is not access. Fail closed.
                throw new InvalidOperationException("The repository returned an unsupported review outcome.");
        }
    }

    // The rejection reason is free text written for the submitter and cannot be guaranteed free of confidential
    // detail, so no administrative read ever returns it, for approvals or rejections, whatever is stored.
    private static ReviewSubmissionDetail ForReviewer(ReviewSubmissionDetail detail)
    {
        return detail.Decision is { Reason: not null } decision
            ? detail with { Decision = decision with { Reason = null } }
            : detail;
    }
}
