using SmartProperty.Application.Abstractions.Identity;
using SmartProperty.Application.Abstractions.Messaging;
using SmartProperty.Application.Abstractions.Persistence.Repositories;
using SmartProperty.Common.Results;

namespace SmartProperty.Application.Marketplace.ListingSubmissions.GetById;

public sealed class GetMyListingSubmissionByIdQueryHandler(
    ICurrentUser currentUser,
    ISubmitterListingSubmissionReadRepository submissions)
    : IQueryHandler<GetMyListingSubmissionByIdQuery, SubmitterSubmissionDetail>
{
    public async Task<Result<SubmitterSubmissionDetail>> Handle(
        GetMyListingSubmissionByIdQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        // Fails closed on an unusable subject instead of reading anyone's submission.
        if (currentUser.UserId is not { } actingUserId || actingUserId == Guid.Empty)
        {
            return Result<SubmitterSubmissionDetail>.Failure(ListingSubmissionErrors.Unauthorized);
        }

        // No submission can have the empty id, so asking the database would only confirm what is already known.
        if (query.SubmissionId == Guid.Empty)
        {
            return Result<SubmitterSubmissionDetail>.Failure(ListingSubmissionErrors.NotFound);
        }

        var submission = await submissions.GetBySubmitterAsync(actingUserId, query.SubmissionId, cancellationToken);

        // One answer for a missing submission and for another user's submission: the repository returns null for
        // both, so the caller cannot tell them apart.
        if (submission is null)
        {
            return Result<SubmitterSubmissionDetail>.Failure(ListingSubmissionErrors.NotFound);
        }

        // A repository that answers with a different submission than the one asked for is a data-integrity fault.
        // It is never shown and never turned into a "not found".
        if (submission.SubmissionId != query.SubmissionId)
        {
            throw new InvalidOperationException("The repository returned a submission that was not requested.");
        }

        return Result<SubmitterSubmissionDetail>.Success(ForSubmitter(submission));
    }

    // The reason is the submitter's to see only for a rejection. An approval never exposes one, whatever is stored.
    private static SubmitterSubmissionDetail ForSubmitter(SubmitterSubmissionDetail submission)
    {
        return submission.Decision is { Approved: true, Reason: not null } decision
            ? submission with { Decision = decision with { Reason = null } }
            : submission;
    }
}
