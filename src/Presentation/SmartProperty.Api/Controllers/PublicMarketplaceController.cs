#nullable enable
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SmartProperty.Api.Contracts;
using SmartProperty.Api.Contracts.Marketplace.Public;
using SmartProperty.Api.Infrastructure;
using SmartProperty.Api.Infrastructure.Errors;
using SmartProperty.Application.Abstractions.Identity;
using SmartProperty.Application.Abstractions.Messaging;
using SmartProperty.Application.Marketplace.PublicListings;
using SmartProperty.Application.Marketplace.PublicListings.GetById;
using SmartProperty.Application.Marketplace.PublicListings.Search;
using SmartProperty.Common.Pagination;
using SmartProperty.Domain.Marketplace;
using SmartProperty.Domain.PropertyRegistry;

namespace SmartProperty.Api.Controllers;

/// <summary>
/// The anonymous, read-only public marketplace: search and detail of Published, Open listings.
/// </summary>
/// <remarks>
/// Anonymous on purpose, like the workspace registration options: visitors browse before they have an account. The
/// whole controller is <c>[AllowAnonymous]</c>, so no operation here asks for a token and the OpenAPI document adds
/// no Bearer requirement to them.
///
/// The actions are deliberately thin: bind, hand one query to its handler, map the result. Which listings are
/// visible, which snapshot supplies their content, and every filter, count, order and page are decided by the
/// handler and the read repository, never here. This controller touches no database context and holds no
/// authorization logic. Only the fields of the public response DTOs ever leave it, and a read model that is
/// incomplete or inconsistent is never shown: the mapper throws and the existing exception handler answers a
/// generic 500.
/// </remarks>
[ApiController]
[AllowAnonymous]
[Route("api/public/marketplace/listings")]
public sealed class PublicMarketplaceController : ControllerBase
{
    /// <summary>
    /// One page of public listings, newest first unless another sort is requested.
    /// </summary>
    /// <remarks>
    /// Public, with an optional sign-in: with a valid token each listing also carries the viewer's
    /// <c>matchPercentage</c> (latest, omitted when none) and <c>isFavorite</c>, and <c>minMatchPercentage</c> and
    /// <c>sort=Match</c> become available; without one they are refused with 422.
    /// Filters (all optional, all combined with AND): <c>propertyType</c> repeated or comma-separated, by name or
    /// number, matches any of them; <c>q</c> is free text (at most 100 characters and 5 words), every word in the
    /// title, description or a place name in English or Arabic; <c>areaId</c>, <c>cityId</c>, <c>compoundId</c>
    /// restrict the place; <c>district</c> is the listing's district or the area's English or Arabic name;
    /// <c>minBedrooms</c>/<c>maxBedrooms</c> (send only the minimum for "5+"); <c>minMatchPercentage</c> (0–100).
    /// Each listing also carries <c>propertyId</c> (for favourites and sharing), <c>coverImageUrl</c>,
    /// <c>bedrooms</c>, <c>bathrooms</c> and <c>areaSqm</c> when known.
    /// An unparseable enum, number or page value is a request-shape failure answered by the standard
    /// <c>400 request.malformed</c>. A well-formed but invalid combination (a price filter or price sort without a
    /// purpose and currency, a Rent price comparison without <c>rentPeriod</c>, <c>rentPeriod</c> without the Rent
    /// purpose, a bad country code or an inverted price range) is a <c>422 validation.failed</c> from the use case.
    /// </remarks>
    [HttpGet]
    [ProducesResponseType<PagedResponse<PublicListingSummaryResponse>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> List(
        [FromServices] IQueryHandler<SearchPublicListingsQuery, PagedList<PublicListingSummary>> handler,
        [FromServices] ICurrentUser currentUser,
        CancellationToken cancellationToken,
        [FromQuery] ListingPurpose? purpose = null,
        // Read as text and parsed here, so "Villa,Apartment" is two types and never their bitwise combination.
        [FromQuery(Name = "propertyType")] string[]? propertyTypes = null,
        [FromQuery] string? countryCode = null,
        [FromQuery] string? region = null,
        [FromQuery] string? city = null,
        [FromQuery] string? district = null,
        [FromQuery] decimal? minPrice = null,
        [FromQuery] decimal? maxPrice = null,
        [FromQuery] string? currency = null,
        [FromQuery] RentPeriod? rentPeriod = null,
        [FromQuery] PublicListingSortOrder? sort = null,
        [FromQuery] int? page = null,
        [FromQuery] int? pageSize = null,
        [FromQuery] DateOnly? availableFrom = null,
        [FromQuery] DateOnly? availableTo = null,
        [FromQuery(Name = "q")] string? search = null,
        [FromQuery] Guid? areaId = null,
        [FromQuery] Guid? cityId = null,
        [FromQuery] Guid? compoundId = null,
        [FromQuery] int? minBedrooms = null,
        [FromQuery] int? maxBedrooms = null,
        [FromQuery] decimal? minMatchPercentage = null)
    {
        if (!QueryEnumList.TryParse<PropertyType>(propertyTypes, out var parsedTypes))
        {
            return BadRequest(ApiErrorResponseFactory.MalformedRequest(HttpContext));
        }

        var query = new SearchPublicListingsQuery(
            purpose,
            null,
            countryCode,
            region,
            city,
            district,
            minPrice,
            maxPrice,
            currency,
            rentPeriod,
            sort,
            page,
            pageSize,
            availableFrom,
            availableTo,
            parsedTypes,
            search,
            areaId,
            cityId,
            compoundId,
            minBedrooms,
            maxBedrooms,
            minMatchPercentage,
            currentUser.UserId);

        var result = await handler.Handle(query, cancellationToken);

        if (result.IsFailure)
        {
            var errorResponse = ApiErrorResponseFactory.FromError(result.Error!, HttpContext);
            return StatusCode(errorResponse.Status, errorResponse);
        }

        var pagedList = result.Value;
        var items = pagedList.Items
            .Select(PublicListingMapper.ToSummaryResponse)
            .ToArray();

        return Ok(PagedResponse<PublicListingSummaryResponse>.FromPagedList(
            PagedList<PublicListingSummaryResponse>.Create(
                items,
                pagedList.PageNumber,
                pagedList.PageSize,
                pagedList.TotalCount)));
    }

    /// <summary>
    /// The public listings page grouped by property: one card per property, with the property's listings that match.
    /// </summary>
    /// <remarks>
    /// Takes exactly the filters, sorts, paging and optional sign-in of <c>GET /api/public/marketplace/listings</c>.
    /// A property is on the page when at least one of its listings matches every filter, and its card carries only the
    /// listings that matched (Sale first): a property offered for Sale and Rent shows both, unless a filter such as
    /// <c>purpose</c>, a price range, <c>rentPeriod</c> or the stay dates leaves only one. A price sort orders cards by
    /// the lowest (ascending) or highest (descending) matching price, <c>Newest</c> by the latest matching publication.
    /// <c>totalCount</c> counts properties.
    /// </remarks>
    [HttpGet("~/api/public/marketplace/properties")]
    [ProducesResponseType<PagedResponse<PublicPropertyCardResponse>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> ListByProperty(
        [FromServices] IQueryHandler<SearchPublicPropertiesQuery, PagedList<PublicPropertyCard>> handler,
        [FromServices] ICurrentUser currentUser,
        CancellationToken cancellationToken,
        [FromQuery] ListingPurpose? purpose = null,
        // Read as text and parsed here, so "Villa,Apartment" is two types and never their bitwise combination.
        [FromQuery(Name = "propertyType")] string[]? propertyTypes = null,
        [FromQuery] string? countryCode = null,
        [FromQuery] string? region = null,
        [FromQuery] string? city = null,
        [FromQuery] string? district = null,
        [FromQuery] decimal? minPrice = null,
        [FromQuery] decimal? maxPrice = null,
        [FromQuery] string? currency = null,
        [FromQuery] RentPeriod? rentPeriod = null,
        [FromQuery] PublicListingSortOrder? sort = null,
        [FromQuery] int? page = null,
        [FromQuery] int? pageSize = null,
        [FromQuery] DateOnly? availableFrom = null,
        [FromQuery] DateOnly? availableTo = null,
        [FromQuery(Name = "q")] string? search = null,
        [FromQuery] Guid? areaId = null,
        [FromQuery] Guid? cityId = null,
        [FromQuery] Guid? compoundId = null,
        [FromQuery] int? minBedrooms = null,
        [FromQuery] int? maxBedrooms = null,
        [FromQuery] decimal? minMatchPercentage = null)
    {
        if (!QueryEnumList.TryParse<PropertyType>(propertyTypes, out var parsedTypes))
        {
            return BadRequest(ApiErrorResponseFactory.MalformedRequest(HttpContext));
        }

        var listingSearch = new SearchPublicListingsQuery(
            purpose, null, countryCode, region, city, district, minPrice, maxPrice, currency, rentPeriod, sort, page,
            pageSize, availableFrom, availableTo, parsedTypes, search, areaId, cityId, compoundId, minBedrooms,
            maxBedrooms, minMatchPercentage, currentUser.UserId);

        var result = await handler.Handle(new SearchPublicPropertiesQuery(listingSearch), cancellationToken);

        if (result.IsFailure)
        {
            var errorResponse = ApiErrorResponseFactory.FromError(result.Error!, HttpContext);
            return StatusCode(errorResponse.Status, errorResponse);
        }

        var pagedList = result.Value;

        return Ok(PagedResponse<PublicPropertyCardResponse>.FromPagedList(
            PagedList<PublicPropertyCardResponse>.Create(
                pagedList.Items.Select(PublicListingMapper.ToCardResponse).ToArray(),
                pagedList.PageNumber,
                pagedList.PageSize,
                pagedList.TotalCount)));
    }

    /// <summary>
    /// One public listing by its public identifier.
    /// </summary>
    /// <remarks>
    /// Every listing that cannot be shown publicly (missing, unpublished, withdrawn, reserved, closed, unapproved)
    /// is the same <c>404 public_listings.not_found</c>, so the response never reveals which case applies. A value
    /// that is not a GUID matches no route at all.
    /// </remarks>
    [HttpGet("{listingId:guid}")]
    [ProducesResponseType<PublicListingDetailResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetById(
        Guid listingId,
        [FromServices] IQueryHandler<GetPublicListingByIdQuery, PublicListingDetail> handler,
        [FromServices] ICurrentUser currentUser,
        CancellationToken cancellationToken)
    {
        var result = await handler.Handle(new GetPublicListingByIdQuery(listingId, currentUser.UserId), cancellationToken);

        if (result.IsFailure)
        {
            var errorResponse = ApiErrorResponseFactory.FromError(result.Error!, HttpContext);
            return StatusCode(errorResponse.Status, errorResponse);
        }

        return Ok(PublicListingMapper.ToDetailResponse(result.Value));
    }
}
