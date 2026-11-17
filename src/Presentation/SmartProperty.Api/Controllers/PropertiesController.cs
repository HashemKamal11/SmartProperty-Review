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
using SmartProperty.Application.PropertyRegistry.Create;
using SmartProperty.Application.PropertyRegistry.GetById;
using SmartProperty.Application.PropertyRegistry.List;
using SmartProperty.Application.PropertyRegistry.Share;
using SmartProperty.Api.Infrastructure.Sharing;
using Microsoft.Extensions.Options;
using SmartProperty.Application.PropertyRegistry.Update;
using SmartProperty.Common.Pagination;
using SmartProperty.Common.Results;
using SmartProperty.Domain.PropertyRegistry;

namespace SmartProperty.Api.Controllers;

/// <summary>
/// The canonical platform-global property registry.
/// </summary>
/// <remarks>
/// Each action declares its own permission rather than the controller declaring one for all: the attribute allows
/// multiples and every one must pass, so a controller-level requirement would silently be added to every action.
/// Reading needs a sign-in and is scoped by <see cref="PropertyReadAccess"/>: an administrator (<c>property.update</c>)
/// reads every property, anyone else only the ones they registered; both see every status unless <c>status</c> is
/// given. Visitors see properties only through published listings (<c>/api/public/marketplace/listings</c>). Creating requires
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
        if (RefuseRemovedCommercialFields(request.UnmatchedMembers) is { } removedFields)
        {
            return removedFields;
        }

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
            request.AreaSqm,
            request.Floor,
            request.Furnished,
            request.TimeZoneId,
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
            ToPhysicalResponse(property.Physical),
            ToPlaceResponse(property.Place),
            []));
    }

    /// <summary>
    /// One page of the registry. Without a <paramref name="status"/> every status is listed (Active and Archived
    /// together) and each item carries its <c>status</c>; send <c>status=Active</c> or <c>status=Archived</c> for one.
    /// The default order is <c>Relevance</c>.
    /// </summary>
    /// <remarks>
    /// This lists physical properties. Prices, the transaction type and availability belong to marketplace
    /// listings, so they are filtered and sorted in <c>GET /api/public/marketplace/listings</c>, not here.
    /// <c>q</c> is free text (at most 100 characters and 5 words): every word must appear in the address (city,
    /// region, district, address line) or in the English or Arabic name of the area, city, country or compound.
    /// <c>sort</c> is <c>Relevance</c> (default: the signed-in user's latest stored match percentage first, then the
    /// better text match, otherwise newest) or <c>Newest</c>.
    /// Filters (all optional, all combined with AND):
    /// <c>type</c> and <c>finishingStatus</c> may be repeated (<c>?type=Villa&amp;type=Apartment</c>) or
    /// comma-separated (<c>?type=Villa,Apartment</c>), by name (any case) or number, and match any of them;
    /// <c>district</c> is the area's English or Arabic name (case-insensitive); <c>areaId</c>, <c>cityId</c> and
    /// <c>compoundId</c> restrict the place; <c>minBedrooms</c>/<c>maxBedrooms</c>,
    /// <c>minBathrooms</c>/<c>maxBathrooms</c> and <c>minAreaSqm</c>/<c>maxAreaSqm</c> are inclusive bounds (send only
    /// the minimum for "5+"); <c>furnished</c> is <c>true</c> or <c>false</c>; <c>minMatchPercentage</c> (0–100) keeps
    /// properties whose latest stored match for the signed-in user is at least that value.
    /// When the caller is signed in, each item also carries <c>matchPercentage</c> (their latest match, omitted when
    /// there is none).
    /// An unparseable enum, number, id or page value is a request-shape failure answered by the standard
    /// <c>400 request.malformed</c>; an invalid combination (a bad country code, an inverted range, an empty id)
    /// is a <c>422 validation.failed</c> from the use case.
    /// </remarks>
    [HttpGet]
    [Authorize]
    [ProducesResponseType<PagedResponse<PropertyResponse>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> List(
        [FromServices] IQueryHandler<GetPropertiesQuery, PagedList<PropertyDetails>> handler,
        [FromServices] ICurrentUser currentUser,
        [FromServices] PropertyReadAccess access,
        CancellationToken cancellationToken,
        // Read as text and parsed here: the framework's enum binder turns "Villa,Apartment" into the bitwise OR of
        // the two numbers, which is a third, unrelated type.
        [FromQuery(Name = "type")] string[]? types = null,
        [FromQuery] PropertyStatus? status = null,
        [FromQuery] string? countryCode = null,
        [FromQuery] int? page = null,
        [FromQuery] int? pageSize = null,
        [FromQuery] string? district = null,
        [FromQuery] Guid? areaId = null,
        [FromQuery] int? minBedrooms = null,
        [FromQuery] int? maxBedrooms = null,
        [FromQuery] decimal? minMatchPercentage = null,
        [FromQuery(Name = "q")] string? search = null,
        [FromQuery] PropertyListSortOrder? sort = null,
        [FromQuery] Guid? cityId = null,
        [FromQuery] Guid? compoundId = null,
        [FromQuery] int? minBathrooms = null,
        [FromQuery] int? maxBathrooms = null,
        [FromQuery] bool? furnished = null,
        [FromQuery(Name = "finishingStatus")] string[]? finishingStatuses = null,
        [FromQuery] decimal? minAreaSqm = null,
        [FromQuery] decimal? maxAreaSqm = null)
    {
        if (currentUser.UserId is not { } readerId)
        {
            return Unauthorized(ApiErrorResponseFactory.Unauthorized(HttpContext));
        }

        if (!TryParseEnumList<PropertyType>(types, out var parsedTypes)
            || !TryParseEnumList<FinishingStatus>(finishingStatuses, out var parsedFinishing))
        {
            return BadRequest(ApiErrorResponseFactory.MalformedRequest(HttpContext));
        }

        // An administrator lists every property; anyone else only the ones they registered.
        var ownerScope = await access.OwnerScopeAsync(readerId, cancellationToken);

        var query = new GetPropertiesQuery(
            parsedTypes,
            status,
            countryCode,
            page,
            pageSize,
            district,
            areaId,
            minBedrooms,
            maxBedrooms,
            minMatchPercentage,
            currentUser.UserId,
            search,
            sort,
            ownerScope,
            cityId,
            compoundId,
            minBathrooms,
            maxBathrooms,
            furnished,
            parsedFinishing,
            minAreaSqm,
            maxAreaSqm);

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

    /// <summary>
    /// One property, whatever its status (an Archived property is returned, not hidden). An administrator reads any
    /// property; anyone else only one they registered, and someone else's is <c>404</c> so its existence is not revealed.
    /// </summary>
    [HttpGet("{id:guid}")]
    [Authorize]
    [ProducesResponseType<PropertyResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetById(
        Guid id,
        [FromServices] IQueryHandler<GetPropertyByIdQuery, PropertyDetails> handler,
        [FromServices] ICurrentUser currentUser,
        [FromServices] PropertyReadAccess access,
        CancellationToken cancellationToken)
    {
        if (currentUser.UserId is not { } readerId)
        {
            return Unauthorized(ApiErrorResponseFactory.Unauthorized(HttpContext));
        }

        if (!await access.CanReadAsync(readerId, id, cancellationToken))
        {
            var notFound = ApiErrorResponseFactory.FromError(PropertyReadAccess.NotFound, HttpContext);
            return StatusCode(notFound.Status, notFound);
        }

        var result = await handler.Handle(new GetPropertyByIdQuery(id), cancellationToken);

        if (result.IsFailure)
        {
            var errorResponse = ApiErrorResponseFactory.FromError(result.Error!, HttpContext);
            return StatusCode(errorResponse.Status, errorResponse);
        }

        return Ok(ToResponse(result.Value));
    }

    /// <summary>
    /// What the share button needs for one property: the link that opens it and its cover image. Anonymous. Only an
    /// Active property with a published marketplace listing can be shared: the public sees properties only through
    /// their listings.
    /// </summary>
    /// <remarks>
    /// <c>url</c> is always absolute: <c>Share:PublicBaseUrl</c> when configured, otherwise the request origin; <c>path</c> is
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
            shareOptions.Value.BuildUrl(property.Id, $"{Request.Scheme}://{Request.Host}"),
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
        if (RefuseRemovedCommercialFields(request.UnmatchedMembers) is { } removedFields)
        {
            return removedFields;
        }

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
            request.AreaSqm,
            request.Floor,
            request.Furnished,
            request.TimeZoneId,
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

    /// <summary>
    /// Parses repeated and/or comma-separated enum values, each a defined name (any case) or number. False when any
    /// value is not one defined member; "Villa,Apartment" is two values, never their bitwise combination.
    /// </summary>
    private static bool TryParseEnumList<TEnum>(string[]? values, out IReadOnlyList<TEnum>? parsed)
        where TEnum : struct, Enum
    {
        parsed = null;

        if (values is null || values.Length == 0)
        {
            return true;
        }

        var result = new List<TEnum>();

        foreach (var part in values.SelectMany(value => (value ?? "").Split(',')).Select(part => part.Trim()))
        {
            if (part.Length == 0)
            {
                continue;
            }

            if (!Enum.TryParse<TEnum>(part, ignoreCase: true, out var member) || !Enum.IsDefined(member))
            {
                return false;
            }

            result.Add(member);
        }

        parsed = result.Count == 0 ? null : result;

        return true;
    }

    private static PropertyPhysicalResponse ToPhysicalResponse(PropertyPhysicalDetails? physical)
    {
        return physical is null
            ? new PropertyPhysicalResponse(null, null, null, null)
            : new PropertyPhysicalResponse(physical.AreaSqm, physical.Floor, physical.Furnished, physical.TimeZoneId);
    }

    /// <summary>
    /// A request that still sends a member that moved to marketplace listings is refused, never silently ignored.
    /// </summary>
    private ObjectResult? RefuseRemovedCommercialFields(IDictionary<string, System.Text.Json.JsonElement>? unmatched)
    {
        var found = RemovedCommercialFields.Find(unmatched);

        if (found.Count == 0)
        {
            return null;
        }

        var error = new Error("validation.failed", RemovedCommercialFields.Message(found), ErrorType.Validation);
        var errorResponse = ApiErrorResponseFactory.FromError(error, HttpContext);

        return StatusCode(errorResponse.Status, errorResponse);
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
            ToPhysicalResponse(property.Physical),
            ToPlaceResponse(property.Place),
            (property.Images ?? []).Select(image => PropertyImagesController.ToResponse(property.Id, image)).ToArray(),
            property.MatchPercentage);
    }
}
