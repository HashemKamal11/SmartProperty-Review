#nullable enable
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SmartProperty.Api.Contracts;
using SmartProperty.Api.Contracts.PropertyComparison;
using SmartProperty.Api.Infrastructure.Errors;
using SmartProperty.Application.Abstractions.Identity;
using SmartProperty.Application.Abstractions.Messaging;
using SmartProperty.Application.PropertyComparison;
using SmartProperty.Application.PropertyComparison.Add;
using SmartProperty.Application.PropertyComparison.Get;
using SmartProperty.Application.PropertyComparison.Remove;
using SmartProperty.Common.Results;

namespace SmartProperty.Api.Controllers;

/// <summary>
/// The signed-in user's comparison list: two or three properties set side by side before they decide to buy or rent.
/// </summary>
/// <remarks>
/// The list belongs to the user the access token names; there is no user id in any route or body, so one user can
/// never read or change another's. Any signed-in user may compare; no permission is needed. A list holds at most three
/// properties, all for sale or all for rent, and needs at least two to be compared (<c>canCompare</c>).
/// </remarks>
[ApiController]
[Route("api/comparison")]
[Authorize]
public sealed class ComparisonController : ControllerBase
{
    /// <summary>The list, with each property in full and the user's latest AI match for it, in the order they were added.</summary>
    [HttpGet]
    [ProducesResponseType<PropertyComparisonResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> Get(
        [FromServices] ICurrentUser currentUser,
        [FromServices] IQueryHandler<GetComparisonQuery, PropertyComparisonDetails> handler,
        CancellationToken cancellationToken)
    {
        if (currentUser.UserId is not { } userId)
        {
            return Unauthorized(ApiErrorResponseFactory.Unauthorized(HttpContext));
        }

        return ToResult(await handler.Handle(new GetComparisonQuery(userId), cancellationToken));
    }

    /// <summary>
    /// Puts a property on the list. Idempotent: <c>201</c> when it was added, <c>200</c> when it already was. The body is
    /// the whole list either way.
    /// </summary>
    /// <remarks>
    /// An unknown property is <c>404</c>. Conflicts are <c>409</c>: <c>comparison.limit_reached</c> (already three),
    /// <c>comparison.transaction_type_mismatch</c> (a sale with a rent), <c>comparison.property_not_listed</c> (the
    /// property is for neither) and <c>comparison.property_archived</c>.
    /// </remarks>
    [HttpPut("{propertyId:guid}")]
    [ProducesResponseType<PropertyComparisonResponse>(StatusCodes.Status201Created)]
    [ProducesResponseType<PropertyComparisonResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Add(
        Guid propertyId,
        [FromServices] ICurrentUser currentUser,
        [FromServices] ICommandHandler<AddToComparisonCommand, AddToComparisonResult> handler,
        CancellationToken cancellationToken)
    {
        if (currentUser.UserId is not { } userId)
        {
            return Unauthorized(ApiErrorResponseFactory.Unauthorized(HttpContext));
        }

        var result = await handler.Handle(new AddToComparisonCommand(userId, propertyId), cancellationToken);

        if (result.IsFailure)
        {
            return ToError(result.Error!);
        }

        var body = ToResponse(result.Value.Comparison);

        return result.Value.Created
            ? StatusCode(StatusCodes.Status201Created, body)
            : Ok(body);
    }

    /// <summary>Takes a property off the list. Idempotent: <c>200</c> with the remaining list whether or not it was on it.</summary>
    [HttpDelete("{propertyId:guid}")]
    [ProducesResponseType<PropertyComparisonResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> Remove(
        Guid propertyId,
        [FromServices] ICurrentUser currentUser,
        [FromServices] ICommandHandler<RemoveFromComparisonCommand, PropertyComparisonDetails> handler,
        CancellationToken cancellationToken)
    {
        if (currentUser.UserId is not { } userId)
        {
            return Unauthorized(ApiErrorResponseFactory.Unauthorized(HttpContext));
        }

        return ToResult(await handler.Handle(new RemoveFromComparisonCommand(userId, propertyId), cancellationToken));
    }

    /// <summary>Empties the list. <c>200</c> with the now empty list.</summary>
    [HttpDelete]
    [ProducesResponseType<PropertyComparisonResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> Clear(
        [FromServices] ICurrentUser currentUser,
        [FromServices] ICommandHandler<ClearComparisonCommand, PropertyComparisonDetails> handler,
        CancellationToken cancellationToken)
    {
        if (currentUser.UserId is not { } userId)
        {
            return Unauthorized(ApiErrorResponseFactory.Unauthorized(HttpContext));
        }

        return ToResult(await handler.Handle(new ClearComparisonCommand(userId), cancellationToken));
    }

    private IActionResult ToResult(Result<PropertyComparisonDetails> result)
    {
        return result.IsFailure ? ToError(result.Error!) : Ok(ToResponse(result.Value));
    }

    private IActionResult ToError(Error error)
    {
        var errorResponse = ApiErrorResponseFactory.FromError(error, HttpContext);

        return StatusCode(errorResponse.Status, errorResponse);
    }

    private static PropertyComparisonResponse ToResponse(PropertyComparisonDetails comparison)
    {
        return new PropertyComparisonResponse(
            comparison.Items
                .Select(item => new PropertyComparisonItemResponse(
                    item.Position,
                    item.AddedAt,
                    item.MatchPercentage,
                    item.MatchDescription,
                    PropertiesController.ToResponse(item.Property),
                    item.Listings?.Select(SmartProperty.Api.Contracts.Marketplace.Public.PublicListingMapper.ToSummaryResponse).ToArray()))
                .ToArray(),
            comparison.MaxItems,
            comparison.MinItemsToCompare,
            comparison.CanCompare);
    }
}
