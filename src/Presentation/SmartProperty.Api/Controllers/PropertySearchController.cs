#nullable enable
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using SmartProperty.Api.Infrastructure.Security;
using SmartProperty.Api.Contracts;
using SmartProperty.Api.Contracts.PropertyMatching;
using SmartProperty.Api.Infrastructure.Errors;
using SmartProperty.Application.Abstractions.Identity;
using SmartProperty.Application.Abstractions.Messaging;
using SmartProperty.Application.PropertyMatching.Search;

namespace SmartProperty.Api.Controllers;

/// <summary>
/// Free-text property search, answered by the AI search service.
/// </summary>
/// <remarks>
/// Anyone may search, signed in or not. The query goes to the AI service, which ranks the properties. When a signed-in
/// user searched, for every result that is a
/// property of this platform the match — percentage, description, and the query — is stored against the user
/// before the answer is returned, so a user's search history is kept here. An anonymous search stores nothing. Any listing the AI service knows that is
/// not one of this platform's properties is returned for display with a null <c>propertyId</c> and is not stored.
/// </remarks>
[ApiController]
[Route("api/property-search")]
// Anonymous callers trigger a paid upstream AI call and (when signed in) stored rows, so each address is limited.
[EnableRateLimiting(RateLimitingOptions.SearchPolicyName)]
public sealed class PropertySearchController : ControllerBase
{
    [HttpPost]
    [AllowAnonymous]
    [ProducesResponseType<PropertySearchResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status422UnprocessableEntity)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status502BadGateway)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> Search(
        PropertySearchRequest request,
        [FromServices] ICurrentUser currentUser,
        [FromServices] ICommandHandler<SearchPropertiesCommand, PropertySearchResult> handler,
        CancellationToken cancellationToken)
    {
        // Anyone may search. The token's subject, when there is a valid one, is the only user id that counts (a body
        // cannot name another user); without it the search is answered but nothing is stored.
        var userId = currentUser.UserId;

        var result = await handler.Handle(
            new SearchPropertiesCommand(userId, request.Query, request.TopK),
            cancellationToken);

        if (result.IsFailure)
        {
            var errorResponse = ApiErrorResponseFactory.FromError(result.Error!, HttpContext);
            return StatusCode(errorResponse.Status, errorResponse);
        }

        var search = result.Value;

        return Ok(new PropertySearchResponse(
            search.Query,
            search.CorrectedQuery,
            search.TotalAvailable,
            search.Items.Select(item => new PropertySearchItemResponse(
                item.PropertyId,
                item.ExternalId,
                item.AiPropertyId,
                item.MatchPercentage,
                item.Description,
                new PropertySearchDetailsResponse(
                    item.Details.City,
                    item.Details.CityAr,
                    item.Details.Area,
                    item.Details.AreaAr,
                    item.Details.District,
                    item.Details.DistrictAr,
                    item.Details.Compound,
                    item.Details.CompoundAr,
                    item.Details.PropertyType,
                    item.Details.PropertyTypeAr,
                    item.Details.TransactionType,
                    item.Details.TransactionTypeAr,
                    item.Details.Price,
                    item.Details.Currency,
                    item.Details.AreaSqm,
                    item.Details.Bedrooms,
                    item.Details.Bathrooms,
                    item.Details.FinishingStatus,
                    item.Details.Furnished,
                    item.Details.View,
                    item.Details.Amenities,
                    item.Details.ListingStatus,
                    item.Details.TitleAr,
                    item.Details.TitleEn,
                    item.Details.DescriptionAr,
                    item.Details.DescriptionEn),
                item.Property is null ? null : PropertiesController.ToResponse(item.Property),
                item.Listing is null ? null : SmartProperty.Api.Contracts.Marketplace.Public.PublicListingMapper.ToSummaryResponse(item.Listing))).ToArray()));
    }
}
