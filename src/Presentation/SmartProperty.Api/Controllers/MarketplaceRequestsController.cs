#nullable enable
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SmartProperty.Api.Contracts;
using SmartProperty.Api.Contracts.Marketplace.Requests;
using SmartProperty.Api.Infrastructure.Errors;
using SmartProperty.Application.Abstractions.Messaging;
using SmartProperty.Application.Marketplace.Requests;
using SmartProperty.Application.Marketplace.Requests.Create;
using SmartProperty.Application.Marketplace.Requests.Decide;
using SmartProperty.Application.Marketplace.Requests.Read;
using SmartProperty.Common.Pagination;
using SmartProperty.Common.Results;
using SmartProperty.Domain.Marketplace;

namespace SmartProperty.Api.Controllers;

/// <summary>Shared failure rendering for the marketplace request controllers.</summary>
public abstract class MarketplaceControllerBase : ControllerBase
{
    protected IActionResult Fail(Error error)
    {
        var response = ApiErrorResponseFactory.FromError(error, HttpContext);

        return StatusCode(response.Status, response);
    }
}

/// <summary>
/// A customer's purchase and rental requests: make one, read their own, withdraw one.
/// </summary>
/// <remarks>
/// <para>
/// <b>Identity.</b> The customer is always the authenticated, Active user. No request carries a user, owner, price
/// for a rental, or authority, and any such member is ignored.
/// </para>
/// <para>
/// <b>What a request can be made against.</b> A published, open listing whose advertiser still holds a valid advertising
/// grant, on the version it currently publishes. Anything else, including an unpublished or closed listing, is the same
/// <c>404 marketplace_requests.listing_unavailable</c>. A customer cannot request their own listing.
/// </para>
/// <para>
/// <b>Rentals are dated.</b> A rental request names a pricing period and a start and end date; it is checked against the
/// listing's availability windows, minimum stay and the property's own calendar day, and against commitments in force.
/// The price is copied from the chosen pricing option. A purchase offer defaults to the listing's price.
/// </para>
/// <para>
/// <b>Reads are self-scoped.</b> Another customer's request and a missing one are the same <c>404</c>.
/// </para>
/// </remarks>
[ApiController]
[Authorize]
[Route("api/marketplace")]
public sealed class MarketplaceRequestsController : MarketplaceControllerBase
{
    [HttpPost("purchase-requests")]
    [ProducesResponseType<RequestCreatedResponse>(StatusCodes.Status201Created)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> CreatePurchaseRequest(
        CreatePurchaseRequestRequest request,
        [FromServices] ICommandHandler<CreatePurchaseRequestCommand, RequestCreatedResult> handler,
        CancellationToken cancellationToken)
    {
        if (request.ListingId is not { } listingId)
        {
            return Fail(RequestContractMapper.Invalid("The listing is required."));
        }

        var result = await handler.Handle(
            new CreatePurchaseRequestCommand(listingId, request.OfferedAmount, request.Message), cancellationToken);

        return Created(result);
    }

    [HttpPost("rental-requests")]
    [ProducesResponseType<RequestCreatedResponse>(StatusCodes.Status201Created)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> CreateRentalRequest(
        CreateRentalRequestRequest request,
        [FromServices] ICommandHandler<CreateRentalRequestCommand, RequestCreatedResult> handler,
        CancellationToken cancellationToken)
    {
        if (request.ListingId is not { } listingId
            || request.StartDate is not { } start
            || request.EndDate is not { } end
            || !RequestContractMapper.TryParse<RentPeriod>(request.PricingPeriod, out var period))
        {
            return Fail(RequestContractMapper.Invalid(
                "A listing, a pricing period (Daily, Weekly, Monthly or Yearly), a start date and an end date are required."));
        }

        var result = await handler.Handle(
            new CreateRentalRequestCommand(listingId, period, start, end, request.Message), cancellationToken);

        return Created(result);
    }

    /// <summary>The caller's own requests of both kinds, newest first.</summary>
    [HttpGet("me/requests")]
    [ProducesResponseType<PagedResponse<MarketplaceRequestResponse>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> ListMine(
        [FromServices] IQueryHandler<GetMyRequestsQuery, PagedList<MarketplaceRequestView>> handler,
        CancellationToken cancellationToken,
        [FromQuery] string? kind = null,
        [FromQuery] string? status = null,
        [FromQuery] int? page = null,
        [FromQuery] int? pageSize = null)
    {
        if (!TryParseOptional<RequestKind>(kind, out var parsedKind) || !TryParseOptional<RequestStatusFilter>(status, out var parsedStatus))
        {
            return Fail(RequestContractMapper.Invalid("The kind or status is not supported."));
        }

        var result = await handler.Handle(new GetMyRequestsQuery(parsedKind, parsedStatus, page, pageSize), cancellationToken);

        return result.IsFailure ? Fail(result.Error!) : Ok(ToPage(result.Value));
    }

    [HttpGet("me/purchase-requests/{requestId:guid}")]
    [ProducesResponseType<MarketplaceRequestResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status404NotFound)]
    public Task<IActionResult> GetMyPurchaseRequest(
        Guid requestId,
        [FromServices] IQueryHandler<GetMyRequestQuery, MarketplaceRequestView> handler,
        CancellationToken cancellationToken) => GetOne(RequestKind.Purchase, requestId, handler, cancellationToken);

    [HttpGet("me/rental-requests/{requestId:guid}")]
    [ProducesResponseType<MarketplaceRequestResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status404NotFound)]
    public Task<IActionResult> GetMyRentalRequest(
        Guid requestId,
        [FromServices] IQueryHandler<GetMyRequestQuery, MarketplaceRequestView> handler,
        CancellationToken cancellationToken) => GetOne(RequestKind.Rental, requestId, handler, cancellationToken);

    [HttpPost("me/purchase-requests/{requestId:guid}/withdraw")]
    [ProducesResponseType<RequestDecisionResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status409Conflict)]
    public Task<IActionResult> WithdrawPurchaseRequest(
        Guid requestId,
        [FromServices] ICommandHandler<WithdrawRequestCommand, RequestDecisionResult> handler,
        CancellationToken cancellationToken) => Withdraw(RequestKind.Purchase, requestId, handler, cancellationToken);

    [HttpPost("me/rental-requests/{requestId:guid}/withdraw")]
    [ProducesResponseType<RequestDecisionResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status409Conflict)]
    public Task<IActionResult> WithdrawRentalRequest(
        Guid requestId,
        [FromServices] ICommandHandler<WithdrawRequestCommand, RequestDecisionResult> handler,
        CancellationToken cancellationToken) => Withdraw(RequestKind.Rental, requestId, handler, cancellationToken);

    private async Task<IActionResult> GetOne(
        RequestKind kind,
        Guid requestId,
        IQueryHandler<GetMyRequestQuery, MarketplaceRequestView> handler,
        CancellationToken cancellationToken)
    {
        var result = await handler.Handle(new GetMyRequestQuery(kind, requestId), cancellationToken);

        return result.IsFailure ? Fail(result.Error!) : Ok(RequestContractMapper.ToResponse(result.Value));
    }

    private async Task<IActionResult> Withdraw(
        RequestKind kind,
        Guid requestId,
        ICommandHandler<WithdrawRequestCommand, RequestDecisionResult> handler,
        CancellationToken cancellationToken)
    {
        var result = await handler.Handle(new WithdrawRequestCommand(kind, requestId), cancellationToken);

        return result.IsFailure ? Fail(result.Error!) : Ok(RequestContractMapper.ToResponse(result.Value));
    }

    private IActionResult Created(Result<RequestCreatedResult> result)
    {
        if (result.IsFailure)
        {
            return Fail(result.Error!);
        }

        var created = result.Value;

        return StatusCode(
            StatusCodes.Status201Created,
            new RequestCreatedResponse(created.Kind.ToString(), created.RequestId, created.ListingId, created.Status, created.SubmittedAt));
    }

    internal static bool TryParseOptional<T>(string? text, out T? value)
        where T : struct, Enum
    {
        value = null;

        if (string.IsNullOrWhiteSpace(text))
        {
            return true;
        }

        if (!RequestContractMapper.TryParse<T>(text, out var parsed))
        {
            return false;
        }

        value = parsed;

        return true;
    }

    internal static PagedResponse<MarketplaceRequestResponse> ToPage(PagedList<MarketplaceRequestView> page) =>
        ToPage(page, (MarketplaceRequestView view) => RequestContractMapper.ToResponse(view));

    internal static PagedResponse<TOut> ToPage<TIn, TOut>(PagedList<TIn> page, Func<TIn, TOut> map) =>
        PagedResponse<TOut>.FromPagedList(
            PagedList<TOut>.Create(page.Items.Select(map).ToArray(), page.PageNumber, page.PageSize, page.TotalCount));
}
