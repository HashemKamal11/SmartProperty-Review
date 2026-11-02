#nullable enable
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SmartProperty.Api.Contracts;
using SmartProperty.Api.Contracts.PropertyRegistry;
using SmartProperty.Api.Infrastructure.Errors;
using SmartProperty.Application.Abstractions.Messaging;
using SmartProperty.Application.Locations;

namespace SmartProperty.Api.Controllers;

/// <summary>
/// The place picker, one level at a time: countries, then the cities of a country, the areas of a city, and the
/// compounds of an area. A property is placed by sending the id of its area (and compound) as <c>areaId</c> and
/// <c>compoundId</c>.
/// </summary>
/// <remarks>
/// The places are this platform's own rows, copied from the AI search service the first time a level is asked for,
/// so the ids returned here are this platform's ids. Once a level is stored it is answered without calling the AI
/// service.
/// </remarks>
[ApiController]
[Route("api/locations")]
public sealed class LocationsController : ControllerBase
{
    /// <param name="level">country (default), city, area, or compound.</param>
    /// <param name="parentId">The id of the country for cities, the city for areas, the area for compounds.</param>
    [HttpGet]
    [Authorize]
    [ProducesResponseType<IReadOnlyList<PlaceResponse>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status422UnprocessableEntity)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status502BadGateway)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> List(
        [FromServices] IQueryHandler<GetLocationsQuery, IReadOnlyList<LocationDetails>> handler,
        CancellationToken cancellationToken,
        [FromQuery] string? level = null,
        [FromQuery] Guid? parentId = null)
    {
        var result = await handler.Handle(new GetLocationsQuery(level, parentId), cancellationToken);

        if (result.IsFailure)
        {
            var errorResponse = ApiErrorResponseFactory.FromError(result.Error!, HttpContext);
            return StatusCode(errorResponse.Status, errorResponse);
        }

        return Ok(result.Value
            .Select(place => new PlaceResponse(place.Id, place.NameEn, place.NameAr))
            .ToArray());
    }
}

public sealed record PlaceResponse(Guid Id, string NameEn, string NameAr);
