#nullable enable
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SmartProperty.Api.Contracts;
using SmartProperty.Api.Contracts.PropertyRegistry;
using SmartProperty.Api.Infrastructure.Errors;
using SmartProperty.Application.Abstractions.Messaging;
using SmartProperty.Application.PropertyRegistry;
using SmartProperty.Application.PropertyRegistry.Views;

namespace SmartProperty.Api.Controllers;

/// <summary>
/// The fixed list of views a property can have (city, garden, sea, ...). Reference data, so any signed-in user may
/// read it, like the amenity list.
/// </summary>
[ApiController]
[Route("api/views")]
public sealed class ViewsController : ControllerBase
{
    [HttpGet]
    [Authorize]
    [ProducesResponseType<IReadOnlyList<ViewResponse>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> List(
        [FromServices] IQueryHandler<GetViewsQuery, IReadOnlyList<ViewDetails>> handler,
        CancellationToken cancellationToken)
    {
        var result = await handler.Handle(new GetViewsQuery(), cancellationToken);

        if (result.IsFailure)
        {
            var errorResponse = ApiErrorResponseFactory.FromError(result.Error!, HttpContext);
            return StatusCode(errorResponse.Status, errorResponse);
        }

        return Ok(result.Value
            .Select(view => new ViewResponse(view.Code, view.NameAr, view.NameEn))
            .ToArray());
    }
}
