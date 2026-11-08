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
using SmartProperty.Application.PropertyRegistry.Share;
using SmartProperty.Api.Infrastructure.Sharing;
using Microsoft.Extensions.Options;
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
    /// One page of the registry. Without a <paramref name="status"/> only Active properties are listed; send
    /// <c>status=Archived</c> to list archived ones. The default order is <c>Relevance</c>.
    /// </summary>
    /// <remarks>
    /// <c>q</c> is free text (at most 100 characters and 5 words): every word must appear in the title, description,
    /// city, region, district, address line or area name. <c>listingStatus</c> restricts to Available, Reserved,
    /// Sold or Rented. <c>sort</c> is <c>Relevance</c> (default: the signed-in user's latest stored match percentage
    /// first, then the better text match, otherwise newest), <c>Newest</c>, <c>PriceAsc</c> or <c>PriceDesc</c>
    /// (properties without a price come last).
    /// Filters (all optional, all combined with AND):
    /// <c>type</c> may be repeated (<c>?type=Villa&amp;type=Apartment</c>) and matches any of them;
    /// <c>district</c> (case-insensitive) and <c>areaId</c> restrict the location;
    /// <c>minPrice</c>/<c>maxPrice</c> bound the listing price; <c>minBedrooms</c>/<c>maxBedrooms</c> bound the
    /// bedroom count (send only <c>minBedrooms=5</c> for "5+"); <c>minMatchPercentage</c> (0–100) keeps properties
    /// whose latest stored match for the signed-in user is at least that value, and requires a signed-in user.
    /// When the caller is signed in, each item also carries <c>matchPercentage</c> (their latest match, omitted when
    /// there is none).
    /// An unparseable enum, number, id or page value is a request-shape failure answered by the standard
    /// <c>400 request.malformed</c>; an invalid combination (a bad country code, an inverted price or bedroom range,
    /// a match filter without a signed-in user) is a <c>422 validation.failed</c> from the use case.
    /// </remarks>
    [HttpGet]
    [AllowAnonymous]
    [ProducesResponseType<PagedResponse<PropertyResponse>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> List(
        [FromServices] IQueryHandler<GetPropertiesQuery, PagedList<PropertyDetails>> handler,
        [FromServices] ICurrentUser currentUser,
        CancellationToken cancellationToken,
        [FromQuery(Name = "type")] PropertyType[]? types = null,
        [FromQuery] PropertyStatus? status = null,
        [FromQuery] string? countryCode = null,
        [FromQuery] int? page = null,
        [FromQuery] int? pageSize = null,
        [FromQuery] string? district = null,
        [FromQuery] Guid? areaId = null,
        [FromQuery] decimal? minPrice = null,
        [FromQuery] decimal? maxPrice = null,
        [FromQuery] int? minBedrooms = null,
        [FromQuery] int? maxBedrooms = null,
        [FromQuery] decimal? minMatchPercentage = null,
        [FromQuery] ListingStatus? listingStatus = null,
        [FromQuery(Name = "q")] string? search = null,
        [FromQuery] PropertyListSortOrder? sort = null)
    {
        var query = new GetPropertiesQuery(
            types,
            status,
            countryCode,
            page,
            pageSize,
            district,
            areaId,
            minPrice,
            maxPrice,
            minBedrooms,
            maxBedrooms,
            minMatchPercentage,
            currentUser.UserId,
            listingStatus,
            search,
            sort);

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
    /// What the share button needs for one property: the link that opens it and a short preview (titles,
    /// descriptions and the cover image). Anonymous. Only an Active property can be shared.
    /// </summary>
    /// <remarks>
    /// <c>url</c> is absolute when <c>Share:PublicBaseUrl</c> is configured and is omitted otherwise; <c>path</c> is
    /// always present. An unknown or archived property is <c>404 properties.not_found</c>.
    /// </remarks>
    [HttpGet("{id:guid}/share")]
    [AllowAnonymous]
    [ProducesResponseType<PropertyShareResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Share(
        Guid id,
        [FromServices] IQueryHandler<GetPropertyShareQuery, PropertyDetails> handler,
        [FromServices] IOptions<ShareOptions> shareOptions,
        CancellationToken cancellationToken)
    {
        var result = await handler.Handle(new GetPropertyShareQuery(id), cancellationToken);

        if (result.IsFailure)
        {
            var errorResponse = ApiErrorResponseFactory.FromError(result.Error!, HttpContext);
            return StatusCode(errorResponse.Status, errorResponse);
        }

        var property = result.Value;
        var images = (property.Images ?? [])
            .Select(image => PropertyImagesController.ToResponse(property.Id, image))
            .ToArray();
        var cover = images.FirstOrDefault(image => image.IsCover) ?? images.FirstOrDefault();

        return Ok(new PropertyShareResponse(
            property.Id,
            shareOptions.Value.BuildPath(property.Id),
            shareOptions.Value.BuildUrl(property.Id),
            property.Listing?.TitleAr,
            property.Listing?.TitleEn,
            property.Listing?.DescriptionAr,
            property.Listing?.DescriptionEn,
            cover?.Url));
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
            (property.Images ?? []).Select(image => PropertyImagesController.ToResponse(property.Id, image)).ToArray(),
            property.MatchPercentage);
    }
}
