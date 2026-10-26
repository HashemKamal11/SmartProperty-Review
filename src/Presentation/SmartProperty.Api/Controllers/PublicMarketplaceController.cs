#nullable enable
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SmartProperty.Api.Contracts;
using SmartProperty.Api.Contracts.Marketplace.Public;
using SmartProperty.Api.Infrastructure.Errors;
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
    /// One page of public listings, newest first unless a price sort is requested.
    /// </summary>
    /// <remarks>
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
        CancellationToken cancellationToken,
        [FromQuery] ListingPurpose? purpose = null,
        [FromQuery] PropertyType? propertyType = null,
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
        [FromQuery] int? pageSize = null)
    {
        var query = new SearchPublicListingsQuery(
            purpose,
            propertyType,
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
            pageSize);

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
        CancellationToken cancellationToken)
    {
        var result = await handler.Handle(new GetPublicListingByIdQuery(listingId), cancellationToken);

        if (result.IsFailure)
        {
            var errorResponse = ApiErrorResponseFactory.FromError(result.Error!, HttpContext);
            return StatusCode(errorResponse.Status, errorResponse);
        }

        return Ok(PublicListingMapper.ToDetailResponse(result.Value));
    }
}
