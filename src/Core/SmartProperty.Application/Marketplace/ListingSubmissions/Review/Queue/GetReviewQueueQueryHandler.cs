using SmartProperty.Application.Abstractions.Authorization;
using SmartProperty.Application.Abstractions.Identity;
using SmartProperty.Application.Abstractions.Messaging;
using SmartProperty.Application.Abstractions.Persistence.Repositories;
using SmartProperty.Common.Pagination;
using SmartProperty.Common.Results;
using SmartProperty.Domain.Marketplace;

namespace SmartProperty.Application.Marketplace.ListingSubmissions.Review.Queue;

public sealed class GetReviewQueueQueryHandler(
    ICurrentUser currentUser,
    IReviewReadAuthorizer authorizer,
    IReviewerListingSubmissionReadRepository submissions)
    : IQueryHandler<GetReviewQueueQuery, PagedList<ReviewQueueItem>>
{
    public async Task<Result<PagedList<ReviewQueueItem>>> Handle(
        GetReviewQueueQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        // Fails closed on an unusable subject instead of reading anything for anyone.
        if (currentUser.UserId is not { } reviewerUserId || reviewerUserId == Guid.Empty)
        {
            return Result<PagedList<ReviewQueueItem>>.Failure(ListingSubmissionErrors.Unauthorized);
        }

        // Authorization comes before validation, so a caller who may not read learns nothing about the request.
        // Only an explicit true allows; an exception from the authorizer is never turned into access.
        if (!await authorizer.IsAuthorizedAsync(reviewerUserId, cancellationToken))
        {
            return Result<PagedList<ReviewQueueItem>>.Failure(ReviewErrors.Forbidden);
        }

        if (GetReviewQueueQueryValidator.Validate(query) is { } validationError)
        {
            return Result<PagedList<ReviewQueueItem>>.Failure(validationError);
        }

        var status = query.Status ?? ListingSubmissionStatus.PendingReview;

        // PageParameters owns both bounds and checked offset arithmetic, so no caller-controlled value can wrap
        // into a negative or multi-billion-row database offset.
        var pageParameters = new PageParameters();

        if (query.Page is { } page)
        {
            pageParameters.PageNumber = page;
        }

        if (query.PageSize is { } pageSize)
        {
            pageParameters.PageSize = pageSize;
        }

        // The reviewer id is the only identity the repository receives, and only to establish independence. Scoping,
        // counting, ordering and paging are its job and happen in the database; the handler adds nothing.
        var result = await submissions.ListForReviewAsync(
            reviewerUserId,
            query.Purpose,
            status,
            pageParameters.Offset,
            pageParameters.PageSize,
            cancellationToken);

        return Result<PagedList<ReviewQueueItem>>.Success(
            PagedList<ReviewQueueItem>.Create(
                result.Items,
                pageParameters.PageNumber,
                pageParameters.PageSize,
                result.TotalCount));
    }
}
