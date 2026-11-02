using SmartProperty.Application.Abstractions.Messaging;
using SmartProperty.Application.Abstractions.Persistence.Repositories;
using SmartProperty.Common.Pagination;
using SmartProperty.Common.Results;

namespace SmartProperty.Application.Favorites.List;

/// <summary>
/// Returns one page of the user's own favorites. Read-only: it saves nothing, and the user id is the one the caller
/// is signed in as, so one user can never read another's list.
/// </summary>
public sealed class GetFavoritesQueryHandler(IFavoritePropertyRepository favoriteRepository)
    : IQueryHandler<GetFavoritesQuery, PagedList<FavoriteDetails>>
{
    public async Task<Result<PagedList<FavoriteDetails>>> Handle(
        GetFavoritesQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        if (query.UserId == Guid.Empty)
        {
            return Result<PagedList<FavoriteDetails>>.Failure(
                FavoriteErrors.ValidationFailed("A signed-in user is required."));
        }

        // PageParameters owns both bounds and checked offset arithmetic, so no caller-controlled value can wrap into a
        // negative or multi-billion-row database offset.
        var pageParameters = new PageParameters();

        if (query.Page is { } page)
        {
            pageParameters.PageNumber = page;
        }

        if (query.PageSize is { } pageSize)
        {
            pageParameters.PageSize = pageSize;
        }

        var result = await favoriteRepository.ListAsync(
            query.UserId,
            pageParameters.Offset,
            pageParameters.PageSize,
            cancellationToken);

        return Result<PagedList<FavoriteDetails>>.Success(
            PagedList<FavoriteDetails>.Create(
                result.Items,
                pageParameters.PageNumber,
                pageParameters.PageSize,
                result.TotalCount));
    }
}

public sealed class GetFavoriteIdsQueryHandler(IFavoritePropertyRepository favoriteRepository)
    : IQueryHandler<GetFavoriteIdsQuery, IReadOnlyList<Guid>>
{
    public async Task<Result<IReadOnlyList<Guid>>> Handle(
        GetFavoriteIdsQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        if (query.UserId == Guid.Empty)
        {
            return Result<IReadOnlyList<Guid>>.Failure(
                FavoriteErrors.ValidationFailed("A signed-in user is required."));
        }

        return Result<IReadOnlyList<Guid>>.Success(
            await favoriteRepository.ListPropertyIdsAsync(query.UserId, cancellationToken));
    }
}
