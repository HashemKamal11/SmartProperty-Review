#nullable enable
using Microsoft.AspNetCore.Mvc;
using SmartProperty.Api.Contracts;
using SmartProperty.Api.Contracts.PropertyRegistry;
using SmartProperty.Api.Infrastructure.Authorization;
using SmartProperty.Api.Infrastructure.Errors;
using SmartProperty.Application.Abstractions.Messaging;
using SmartProperty.Application.Authorization;
using SmartProperty.Application.PropertyRegistry;
using SmartProperty.Application.PropertyRegistry.Create;
using SmartProperty.Application.PropertyRegistry.GetById;
using SmartProperty.Application.PropertyRegistry.List;
using SmartProperty.Common.Pagination;
using SmartProperty.Domain.PropertyRegistry;

namespace SmartProperty.Api.Controllers;

/// <summary>
/// The canonical platform-global property registry.
/// </summary>
/// <remarks>
/// Each action declares its own permission rather than the controller declaring one for all: the attribute allows
/// multiples and every one must pass, so a controller-level requirement would silently be added to every action.
/// Reading must not require <c>property.create</c>, and creating must not require <c>property.read</c>.
/// </remarks>
[ApiController]
[Route("api/properties")]
public sealed class PropertiesController : ControllerBase
{
    [HttpPost]
    [RequirePermission(PermissionCodes.PropertyCreate)]
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

    /// <summary>
    /// One page of the registry, newest first. Without a <paramref name="status"/>, Active and Archived properties
    /// are both listed.
    /// </summary>
    /// <remarks>
    /// An unparseable enum or page value is a request-shape failure answered by the standard
    /// <c>400 request.malformed</c>; an invalid country code is a <c>422 validation.failed</c> from the use case.
    /// </remarks>
    [HttpGet]
    [RequirePermission(PermissionCodes.PropertyRead)]
    [ProducesResponseType<PagedResponse<PropertyResponse>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> List(
        [FromServices] IQueryHandler<GetPropertiesQuery, PagedList<PropertyDetails>> handler,
        CancellationToken cancellationToken,
        [FromQuery] PropertyType? type = null,
        [FromQuery] PropertyStatus? status = null,
        [FromQuery] string? countryCode = null,
        [FromQuery] int? page = null,
        [FromQuery] int? pageSize = null)
    {
        var query = new GetPropertiesQuery(type, status, countryCode, page, pageSize);

        var result = await handler.Handle(query, cancellationToken);

        if (result.IsFailure)
        {
            var errorResponse = ApiErrorResponseFactory.FromError(result.Error!, HttpContext);
            return StatusCode(errorResponse.Status, errorResponse);
        }

        var pagedList = result.Value;
        var items = pagedList.Items
            .Select(ToResponse)
            .ToArray();

        return Ok(PagedResponse<PropertyResponse>.FromPagedList(
            PagedList<PropertyResponse>.Create(
                items,
                pagedList.PageNumber,
                pagedList.PageSize,
                pagedList.TotalCount)));
    }

    /// <summary>One property, whatever its status. An Archived property is returned, not hidden.</summary>
    [HttpGet("{id:guid}")]
    [RequirePermission(PermissionCodes.PropertyRead)]
    [ProducesResponseType<PropertyResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetById(
        Guid id,
        [FromServices] IQueryHandler<GetPropertyByIdQuery, PropertyDetails> handler,
        CancellationToken cancellationToken)
    {
        var result = await handler.Handle(new GetPropertyByIdQuery(id), cancellationToken);

        if (result.IsFailure)
        {
            var errorResponse = ApiErrorResponseFactory.FromError(result.Error!, HttpContext);
            return StatusCode(errorResponse.Status, errorResponse);
        }

        return Ok(ToResponse(result.Value));
    }

    private static PropertyResponse ToResponse(PropertyDetails property)
    {
        return new PropertyResponse(
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
            property.UpdatedAt);
    }
}
