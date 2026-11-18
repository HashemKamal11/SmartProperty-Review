using SmartProperty.Application.Abstractions.Messaging;
using SmartProperty.Application.Abstractions.Persistence.Repositories;
using SmartProperty.Common.Pagination;
using SmartProperty.Common.Results;

namespace SmartProperty.Application.Marketplace.PublicListings.Search;

/// <summary>
/// The public listings page grouped by property: the same filters as <see cref="SearchPublicListingsQuery"/>, one
/// card per property with its matching listings.
/// </summary>
public sealed record SearchPublicPropertiesQuery(SearchPublicListingsQuery Search) : IQuery<PagedList<PublicPropertyCard>>;

public sealed class SearchPublicPropertiesQueryHandler(IPublicListingReadRepository publicListings)
    : IQueryHandler<SearchPublicPropertiesQuery, PagedList<PublicPropertyCard>>
{
    public async Task<Result<PagedList<PublicPropertyCard>>> Handle(
        SearchPublicPropertiesQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(query.Search);

        // Exactly the listing search's rules: a price filter still needs a purpose and a currency, and the match
        // filter and sort still need a signed-in viewer.
        var criteria = SearchPublicListingsQueryHandler.CriteriaOf(query.Search);

        if (criteria.IsFailure)
        {
            return Result<PagedList<PublicPropertyCard>>.Failure(criteria.Error!);
        }

        var pageParameters = new PageParameters();

        if (query.Search.Page is { } page)
        {
            pageParameters.PageNumber = page;
        }

        if (query.Search.PageSize is { } pageSize)
        {
            pageParameters.PageSize = pageSize;
        }

        var result = await publicListings.SearchByPropertyAsync(
            criteria.Value,
            pageParameters.Offset,
            pageParameters.PageSize,
            cancellationToken);

        return Result<PagedList<PublicPropertyCard>>.Success(
            PagedList<PublicPropertyCard>.Create(
                result.Items,
                pageParameters.PageNumber,
                pageParameters.PageSize,
                result.TotalCount));
    }
}
