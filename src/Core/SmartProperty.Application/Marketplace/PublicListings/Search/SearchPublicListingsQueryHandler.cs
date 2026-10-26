using SmartProperty.Application.Abstractions.Messaging;
using SmartProperty.Application.Abstractions.Persistence.Repositories;
using SmartProperty.Common.Pagination;
using SmartProperty.Common.Results;

namespace SmartProperty.Application.Marketplace.PublicListings.Search;

public sealed class SearchPublicListingsQueryHandler(IPublicListingReadRepository publicListings)
    : IQueryHandler<SearchPublicListingsQuery, PagedList<PublicListingSummary>>
{
    public async Task<Result<PagedList<PublicListingSummary>>> Handle(
        SearchPublicListingsQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        var criteria = PublicListingSearchFilterValidator.Validate(
            query.Purpose,
            query.PropertyType,
            query.CountryCode,
            query.Region,
            query.City,
            query.District,
            query.MinPrice,
            query.MaxPrice,
            query.Currency,
            query.RentPeriod,
            query.Sort);

        if (criteria.IsFailure)
        {
            return Result<PagedList<PublicListingSummary>>.Failure(criteria.Error!);
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

        // Visibility, filtering, counting, ordering and paging are the repository's job and happen in the
        // database. The handler adds nothing to the page and removes nothing from it.
        var result = await publicListings.SearchAsync(
            criteria.Value,
            pageParameters.Offset,
            pageParameters.PageSize,
            cancellationToken);

        return Result<PagedList<PublicListingSummary>>.Success(
            PagedList<PublicListingSummary>.Create(
                result.Items,
                pageParameters.PageNumber,
                pageParameters.PageSize,
                result.TotalCount));
    }
}
