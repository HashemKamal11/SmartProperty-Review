#nullable enable
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SmartProperty.Api.Contracts;
using SmartProperty.Api.Contracts.PropertyRegistry;
using SmartProperty.Api.Infrastructure.Authorization;
using SmartProperty.Api.Infrastructure.Errors;
using SmartProperty.Application.Abstractions.Identity;
using SmartProperty.Application.Abstractions.Messaging;
using SmartProperty.Application.Authorization;
using SmartProperty.Application.Locations;
using SmartProperty.Application.PropertyRegistry;
using SmartProperty.Application.PropertyRegistry.Archive;
using SmartProperty.Application.PropertyRegistry.Ai.Sync;
using SmartProperty.Application.PropertyRegistry.Create;
using SmartProperty.Application.PropertyRegistry.GetById;
using SmartProperty.Application.PropertyRegistry.List;
using SmartProperty.Application.PropertyRegistry.Update;
using SmartProperty.Common.Pagination;
using SmartProperty.Domain.PropertyRegistry;

namespace SmartProperty.Api.Controllers;

/// <summary>
/// The canonical platform-global property registry.
/// </summary>
/// <remarks>
/// Each action declares its own permission rather than the controller declaring one for all: the attribute allows
/// multiples and every one must pass, so a controller-level requirement would silently be added to every action.
/// Reading is public: listing and getting one property need no sign-in and no permission. Creating requires
/// <c>property.create</c> alone, updating <c>property.update</c> alone, and archiving <c>property.archive</c> alone.
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
        [FromServices] ICurrentUser currentUser,
        [FromServices] ICommandHandler<CreatePropertyCommand, CreatePropertyResult> handler,
        CancellationToken cancellationToken)
    {
        var command = new CreatePropertyCommand(
            request.Type,
            request.AreaId,
            request.AddressLine,
            request.PostalCode,
            request.Latitude,
            request.Longitude,
            request.Bedrooms,
            request.Bathrooms,
            request.BuiltDate,
            request.FinishingStatus,
            request.AmenityCodes,
            request.ViewCodes,
            request.TransactionType,
            request.Price,
            request.AreaSqm,
            request.Floor,
            request.Furnished,
            request.ListingStatus,
            request.TitleAr,
            request.TitleEn,
            request.DescriptionAr,
            request.DescriptionEn,
            request.CompoundId,
            currentUser.UserId);

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
            property.UpdatedAt,
            property.Bedrooms,
            property.Bathrooms,
            property.BuiltDate,
            property.FinishingStatus?.ToString(),
            ToAmenityResponses(property.Amenities),
            ToViewResponses(property.Views),
            ToListingResponse(property.Listing),
            ToAiSyncResponse(property.AiSync),
            ToPlaceResponse(property.Place),
            []));
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
    [AllowAnonymous]
    [ProducesResponseType<PagedResponse<PropertyResponse>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status400BadRequest)]
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
    [AllowAnonymous]
    [ProducesResponseType<PropertyResponse>(StatusCodes.Status200OK)]
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

    /// <summary>
    /// Corrects the details of one active property by full replacement: an omitted optional field is cleared.
    /// Values identical to the current ones change nothing, including <c>updatedAt</c>.
    /// </summary>
    /// <remarks>
    /// An archived property is refused with <c>409 properties.archived</c>. Status is never changed here.
    /// </remarks>
    [HttpPut("{id:guid}")]
    [RequirePermission(PermissionCodes.PropertyUpdate)]
    [ProducesResponseType<PropertyResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> Update(
        Guid id,
        UpdatePropertyRequest request,
        [FromServices] ICommandHandler<UpdatePropertyCommand, PropertyDetails> handler,
        CancellationToken cancellationToken)
    {
        var command = new UpdatePropertyCommand(
            id,
            request.Type,
            request.AreaId,
            request.AddressLine,
            request.PostalCode,
            request.Latitude,
            request.Longitude,
            request.Bedrooms,
            request.Bathrooms,
            request.BuiltDate,
            request.FinishingStatus,
            request.AmenityCodes,
            request.ViewCodes,
            request.TransactionType,
            request.Price,
            request.AreaSqm,
            request.Floor,
            request.Furnished,
            request.ListingStatus,
            request.TitleAr,
            request.TitleEn,
            request.DescriptionAr,
            request.DescriptionEn,
            request.CompoundId);

        var result = await handler.Handle(command, cancellationToken);

        if (result.IsFailure)
        {
            var errorResponse = ApiErrorResponseFactory.FromError(result.Error!, HttpContext);
            return StatusCode(errorResponse.Status, errorResponse);
        }

        return Ok(ToResponse(result.Value));
    }

    /// <summary>
    /// Sends the property to the AI search service again. The repair for a property whose last attempt failed,
    /// or that was incomplete and has since been completed; safe to repeat.
    /// </summary>
    /// <remarks>
    /// Returns <c>200</c> with the property whatever the outcome; <c>aiSync</c> says whether it is now held. An
    /// archived property is refused with <c>409 properties.archived</c>.
    /// </remarks>
    [HttpPost("{id:guid}/ai-sync")]
    [RequirePermission(PermissionCodes.PropertyUpdate)]
    [ProducesResponseType<PropertyResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> SyncWithAi(
        Guid id,
        [FromServices] ICommandHandler<SyncPropertyWithAiCommand, PropertyDetails> handler,
        CancellationToken cancellationToken)
    {
        var result = await handler.Handle(new SyncPropertyWithAiCommand(id), cancellationToken);

        if (result.IsFailure)
        {
            var errorResponse = ApiErrorResponseFactory.FromError(result.Error!, HttpContext);
            return StatusCode(errorResponse.Status, errorResponse);
        }

        return Ok(ToResponse(result.Value));
    }

    /// <summary>
    /// Archives one active property. The record is kept and stays readable; only its status and
    /// <c>updatedAt</c> change.
    /// </summary>
    /// <remarks>
    /// Not idempotent: an already archived property is refused with <c>409 properties.archived</c>.
    /// </remarks>
    [HttpPost("{id:guid}/archive")]
    [RequirePermission(PermissionCodes.PropertyArchive)]
    [ProducesResponseType<PropertyResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Archive(
        Guid id,
        [FromServices] ICommandHandler<ArchivePropertyCommand, PropertyDetails> handler,
        CancellationToken cancellationToken)
    {
        var result = await handler.Handle(new ArchivePropertyCommand(id), cancellationToken);

        if (result.IsFailure)
        {
            var errorResponse = ApiErrorResponseFactory.FromError(result.Error!, HttpContext);
            return StatusCode(errorResponse.Status, errorResponse);
        }

        return Ok(ToResponse(result.Value));
    }

    private static ViewResponse[] ToViewResponses(IEnumerable<ViewDetails>? views)
    {
        return (views ?? [])
            .Select(view => new ViewResponse(view.Code, view.NameAr, view.NameEn))
            .ToArray();
    }

    private static PropertyListingResponse ToListingResponse(PropertyListingDetails? listing)
    {
        return listing is null
            ? new PropertyListingResponse(null, null, null, null, null, nameof(ListingStatus.Available), null, null, null, null)
            : new PropertyListingResponse(
                listing.TransactionType?.ToString(),
                listing.Price,
                listing.AreaSqm,
                listing.Floor,
                listing.Furnished,
                listing.ListingStatus.ToString(),
                listing.TitleAr,
                listing.TitleEn,
                listing.DescriptionAr,
                listing.DescriptionEn);
    }

    private static PropertyPlaceResponse? ToPlaceResponse(PropertyPlaceDetails? place)
    {
        if (place is null)
        {
            return null;
        }

        return new PropertyPlaceResponse(
            ToItem(place.Country),
            ToItem(place.City),
            ToItem(place.Area),
            place.Compound is null ? null : ToItem(place.Compound));
    }

    private static PlaceResponseItem ToItem(LocationDetails place)
    {
        return new PlaceResponseItem(place.Id, place.NameEn, place.NameAr);
    }

    private static AiSyncResponse ToAiSyncResponse(AiSyncDetails? aiSync)
    {
        return aiSync is null
            ? new AiSyncResponse(nameof(AiSyncStatus.NotSynced), null, null)
            : new AiSyncResponse(aiSync.Status.ToString(), aiSync.SyncedAt, aiSync.Error);
    }

    private static AmenityResponse[] ToAmenityResponses(IEnumerable<AmenityDetails>? amenities)
    {
        return (amenities ?? [])
            .Select(amenity => new AmenityResponse(amenity.Code, amenity.NameAr, amenity.NameEn))
            .ToArray();
    }

    internal static PropertyResponse ToResponse(PropertyDetails property)
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
            property.UpdatedAt,
            property.Bedrooms,
            property.Bathrooms,
            property.BuiltDate,
            property.FinishingStatus?.ToString(),
            ToAmenityResponses(property.Amenities),
            ToViewResponses(property.Views),
            ToListingResponse(property.Listing),
            ToAiSyncResponse(property.AiSync),
            ToPlaceResponse(property.Place),
            (property.Images ?? []).Select(image => PropertyImagesController.ToResponse(property.Id, image)).ToArray());
    }
}
