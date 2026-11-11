#nullable enable
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SmartProperty.Api.Contracts;
using SmartProperty.Api.Contracts.Favorites;
using SmartProperty.Api.Infrastructure.Errors;
using SmartProperty.Application.Abstractions.Identity;
using SmartProperty.Application.Abstractions.Messaging;
using SmartProperty.Application.Favorites;
using SmartProperty.Application.Favorites.Add;
using SmartProperty.Application.Favorites.List;
using SmartProperty.Application.Favorites.Remove;
using SmartProperty.Common.Pagination;

namespace SmartProperty.Api.Controllers;

/// <summary>
/// The signed-in user's favorite properties.
/// </summary>
/// <remarks>
/// A favorite belongs to the user the access token names; there is no user id in any route or body, so one user can
/// never read or change another's list. Any signed-in user may keep favorites; no permission is needed, because the
/// list is the user's own.
/// </remarks>
[ApiController]
[Route("api/favorites")]
[Authorize]
public sealed class FavoritesController : ControllerBase
{
    /// <summary>One page of the user's favorites, most recently added first. Archived properties are included.</summary>
    [HttpGet]
    [ProducesResponseType<PagedResponse<FavoriteResponse>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> List(
        [FromServices] ICurrentUser currentUser,
        [FromServices] IQueryHandler<GetFavoritesQuery, PagedList<FavoriteDetails>> handler,
        CancellationToken cancellationToken,
        [FromQuery] int? page = null,
        [FromQuery] int? pageSize = null)
    {
        if (currentUser.UserId is not { } userId)
        {
            return Unauthorized(ApiErrorResponseFactory.Unauthorized(HttpContext));
        }

        var result = await handler.Handle(new GetFavoritesQuery(userId, page, pageSize), cancellationToken);

        if (result.IsFailure)
        {
            var errorResponse = ApiErrorResponseFactory.FromError(result.Error!, HttpContext);
            return StatusCode(errorResponse.Status, errorResponse);
        }

        var favorites = result.Value;
        var items = favorites.Items
            .Select(favorite => new FavoriteResponse(
                favorite.FavoritedAt,
                PropertiesController.ToResponse(favorite.Property),
                favorite.Listings?.Select(SmartProperty.Api.Contracts.Marketplace.Public.PublicListingMapper.ToSummaryResponse).ToArray()))
            .ToArray();

        return Ok(PagedResponse<FavoriteResponse>.FromPagedList(
            PagedList<FavoriteResponse>.Create(items, favorites.PageNumber, favorites.PageSize, favorites.TotalCount)));
    }

    /// <summary>The ids of every property the user has favorited, for marking them in a list of properties.</summary>
    [HttpGet("ids")]
    [ProducesResponseType<IReadOnlyList<Guid>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> ListIds(
        [FromServices] ICurrentUser currentUser,
        [FromServices] IQueryHandler<GetFavoriteIdsQuery, IReadOnlyList<Guid>> handler,
        CancellationToken cancellationToken)
    {
        if (currentUser.UserId is not { } userId)
        {
            return Unauthorized(ApiErrorResponseFactory.Unauthorized(HttpContext));
        }

        var result = await handler.Handle(new GetFavoriteIdsQuery(userId), cancellationToken);

        if (result.IsFailure)
        {
            var errorResponse = ApiErrorResponseFactory.FromError(result.Error!, HttpContext);
            return StatusCode(errorResponse.Status, errorResponse);
        }

        return Ok(result.Value);
    }

    /// <summary>
    /// Marks a property as a favorite. Idempotent: <c>201</c> when it was added, <c>200</c> when it already was.
    /// </summary>
    /// <remarks>
    /// An unknown property is <c>404</c>. An archived property cannot be added (<c>409 favorites.property_archived</c>),
    /// though one already in the list stays there.
    /// </remarks>
    [HttpPut("{propertyId:guid}")]
    [ProducesResponseType<FavoriteMarkResponse>(StatusCodes.Status201Created)]
    [ProducesResponseType<FavoriteMarkResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Add(
        Guid propertyId,
        [FromServices] ICurrentUser currentUser,
        [FromServices] ICommandHandler<AddFavoriteCommand, AddFavoriteResult> handler,
        CancellationToken cancellationToken)
    {
        if (currentUser.UserId is not { } userId)
        {
            return Unauthorized(ApiErrorResponseFactory.Unauthorized(HttpContext));
        }

        var result = await handler.Handle(new AddFavoriteCommand(userId, propertyId), cancellationToken);

        if (result.IsFailure)
        {
            var errorResponse = ApiErrorResponseFactory.FromError(result.Error!, HttpContext);
            return StatusCode(errorResponse.Status, errorResponse);
        }

        var body = new FavoriteMarkResponse(result.Value.PropertyId, result.Value.FavoritedAt);

        return result.Value.Created
            ? StatusCode(StatusCodes.Status201Created, body)
            : Ok(body);
    }

    /// <summary>
    /// Removes a property from the user's favorites. Idempotent: <c>204</c> whether or not it was one.
    /// </summary>
    [HttpDelete("{propertyId:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> Remove(
        Guid propertyId,
        [FromServices] ICurrentUser currentUser,
        [FromServices] ICommandHandler<RemoveFavoriteCommand, RemoveFavoriteResult> handler,
        CancellationToken cancellationToken)
    {
        if (currentUser.UserId is not { } userId)
        {
            return Unauthorized(ApiErrorResponseFactory.Unauthorized(HttpContext));
        }

        var result = await handler.Handle(new RemoveFavoriteCommand(userId, propertyId), cancellationToken);

        if (result.IsFailure)
        {
            var errorResponse = ApiErrorResponseFactory.FromError(result.Error!, HttpContext);
            return StatusCode(errorResponse.Status, errorResponse);
        }

        return NoContent();
    }
}
