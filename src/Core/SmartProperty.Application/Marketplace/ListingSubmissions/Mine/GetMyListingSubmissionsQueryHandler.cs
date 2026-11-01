using SmartProperty.Application.Abstractions.Identity;
using SmartProperty.Application.Abstractions.Messaging;
using SmartProperty.Application.Abstractions.Persistence.Repositories;
using SmartProperty.Application.Authentication.Me;
using SmartProperty.Common.Pagination;
using SmartProperty.Common.Results;
using SmartProperty.Domain.Identity;

namespace SmartProperty.Application.Marketplace.ListingSubmissions.Mine;

public sealed class GetMyListingSubmissionsQueryHandler(
    ICurrentUser currentUser,
    IUserRepository users,
    ISubmitterListingSubmissionReadRepository submissions)
    : IQueryHandler<GetMyListingSubmissionsQuery, PagedList<SubmitterSubmissionSummary>>
{
    public async Task<Result<PagedList<SubmitterSubmissionSummary>>> Handle(
        GetMyListingSubmissionsQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        // The endpoint requires authentication, so this is normally already satisfied. It is still checked rather
        // than assumed: an unusable subject fails closed instead of reading anyone's submissions.
        if (currentUser.UserId is not { } actingUserId || actingUserId == Guid.Empty)
        {
            return Result<PagedList<SubmitterSubmissionSummary>>.Failure(ListingSubmissionErrors.Unauthorized);
        }

        // A valid token proves who the caller was, not that the account is still in good standing, so the account is
        // read from the persisted user on every request. A token can outlive the user it names: that is the same 401
        // as no token, so the response never confirms an id was real. Any non-active status is the one account-level
        // error, so the exact status is not disclosed. Nothing is read from the submissions repository for either.
        var account = await users.GetByIdAsync(actingUserId, cancellationToken);

        if (account is null)
        {
            return Result<PagedList<SubmitterSubmissionSummary>>.Failure(ListingSubmissionErrors.Unauthorized);
        }

        if (account.Status != UserStatus.Active)
        {
            return Result<PagedList<SubmitterSubmissionSummary>>.Failure(MeErrors.AccountUnavailable);
        }

        if (GetMyListingSubmissionsQueryValidator.Validate(query) is { } validationError)
        {
            return Result<PagedList<SubmitterSubmissionSummary>>.Failure(validationError);
        }

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

        // The acting user is the only identity the repository ever receives. Scoping, counting, ordering and
        // paging are its job and happen in the database; the handler adds nothing to the page.
        var result = await submissions.ListBySubmitterAsync(
            actingUserId,
            query.Purpose,
            query.Status,
            pageParameters.Offset,
            pageParameters.PageSize,
            cancellationToken);

        return Result<PagedList<SubmitterSubmissionSummary>>.Success(
            PagedList<SubmitterSubmissionSummary>.Create(
                result.Items,
                pageParameters.PageNumber,
                pageParameters.PageSize,
                result.TotalCount));
    }
}
