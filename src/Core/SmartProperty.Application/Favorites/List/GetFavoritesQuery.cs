using SmartProperty.Application.Abstractions.Messaging;
using SmartProperty.Common.Pagination;

namespace SmartProperty.Application.Favorites.List;

/// <summary>
/// One page of the user's favorites, most recently added first.
/// </summary>
/// <param name="Page">
/// One-based page number. Null means page 1; a value outside the allowed range is clamped, exactly as
/// <see cref="PageParameters"/> does.
/// </param>
/// <param name="PageSize">Page size. Null means 10; clamped like <paramref name="Page"/>.</param>
public sealed record GetFavoritesQuery(Guid UserId, int? Page, int? PageSize)
    : IQuery<PagedList<FavoriteDetails>>;

/// <summary>The ids of every property the user has favorited, for showing which ones are already favorites.</summary>
public sealed record GetFavoriteIdsQuery(Guid UserId) : IQuery<IReadOnlyList<Guid>>;
