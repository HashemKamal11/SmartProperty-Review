#nullable enable
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SmartProperty.Api.Contracts;
using SmartProperty.Api.Contracts.PropertyRegistry;
using SmartProperty.Api.Infrastructure.Errors;
using SmartProperty.Application.Abstractions.Messaging;
using SmartProperty.Application.PropertyRegistry;
using SmartProperty.Application.PropertyRegistry.Amenities;

namespace SmartProperty.Api.Controllers;

/// <summary>
/// The fixed list of amenities a property can offer (balcony, pool, gym, ...).
/// </summary>
/// <remarks>
/// Reference data, so any signed-in user may read it: the property form needs it to offer the choices, and a
/// buyer or renter filtering by amenity needs the same list. It carries nothing private.
/// </remarks>
[ApiController]
[Route("api/amenities")]
public sealed class AmenitiesController : ControllerBase
{
    [HttpGet]
    [Authorize]
    [ProducesResponseType<IReadOnlyList<AmenityResponse>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> List(
        [FromServices] IQueryHandler<GetAmenitiesQuery, IReadOnlyList<AmenityDetails>> handler,
        CancellationToken cancellationToken)
    {
        var result = await handler.Handle(new GetAmenitiesQuery(), cancellationToken);

        if (result.IsFailure)
        {
            var errorResponse = ApiErrorResponseFactory.FromError(result.Error!, HttpContext);
            return StatusCode(errorResponse.Status, errorResponse);
        }

        return Ok(result.Value
            .Select(amenity => new AmenityResponse(amenity.Code, amenity.NameAr, amenity.NameEn))
            .ToArray());
    }
}
