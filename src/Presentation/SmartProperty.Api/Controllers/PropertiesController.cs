#nullable enable
using Microsoft.AspNetCore.Mvc;
using SmartProperty.Api.Contracts.PropertyRegistry;
using SmartProperty.Api.Infrastructure.Authorization;
using SmartProperty.Api.Infrastructure.Errors;
using SmartProperty.Application.Abstractions.Messaging;
using SmartProperty.Application.Authorization;
using SmartProperty.Application.PropertyRegistry.Create;

namespace SmartProperty.Api.Controllers;

[ApiController]
[Route("api/properties")]
[RequirePermission(PermissionCodes.PropertyCreate)]
public sealed class PropertiesController : ControllerBase
{
    [HttpPost]
    [ProducesResponseType<CreatePropertyResponse>(StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> Create(
        CreatePropertyRequest request,
        [FromServices] ICommandHandler<CreatePropertyCommand, CreatePropertyResult> handler,
        CancellationToken cancellationToken)
    {
        var command = new CreatePropertyCommand(
            request.Type,
            request.CountryCode,
            request.City,
            request.Region,
            request.District,
            request.AddressLine,
            request.PostalCode,
            request.Latitude,
            request.Longitude);

        var result = await handler.Handle(command, cancellationToken);

        if (result.IsFailure)
        {
            var errorResponse = ApiErrorResponseFactory.FromError(result.Error!, HttpContext);
            return StatusCode(errorResponse.Status, errorResponse);
        }

        var property = result.Value;
        return StatusCode(StatusCodes.Status201Created, new CreatePropertyResponse(
            property.Id,
            property.Type.ToString(),
            property.Status.ToString(),
            new PropertyAddressResponse(
                property.Address.CountryCode,
                property.Address.City,
                property.Address.Region,
                property.Address.District,
                property.Address.AddressLine,
                property.Address.PostalCode,
                property.Address.Latitude,
                property.Address.Longitude),
            property.CreatedAt,
            property.UpdatedAt));
    }
}
