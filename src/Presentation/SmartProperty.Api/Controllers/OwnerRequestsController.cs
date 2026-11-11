#nullable enable
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SmartProperty.Api.Contracts;
using SmartProperty.Api.Contracts.Marketplace.Requests;
using SmartProperty.Application.Abstractions.Messaging;
using SmartProperty.Application.Marketplace.Requests;
using SmartProperty.Application.Marketplace.Requests.Decide;
using SmartProperty.Application.Marketplace.Requests.Read;
using SmartProperty.Common.Pagination;

namespace SmartProperty.Api.Controllers;

/// <summary>
/// The advertiser's side: the requests made against listings they advertise, and accepting or rejecting them.
/// </summary>
/// <remarks>
/// <para>
/// <b>Who is the advertiser.</b> The author of the approved version a request was made against, taken from the
/// authenticated user and the stored version, never from the request. A request on anyone else's listing, one from
/// before versions existed, and a missing one are all the same <c>404</c>.
/// </para>
/// <para>
/// <b>Accepting</b> needs the advertiser's grant for the purpose to be in force now, and the request to be against the
/// version the listing publishes now. It commits the property (a dated rental, or a sale), closes the property's
/// current Sale and Rent advertisements, and closes every other open request with its history kept. Commitments already
/// in force, whether signed or not yet signed, are never cancelled. To advertise again the owner submits a new version
/// for review. A sale is refused while a daily or weekly rental is in force; a monthly or yearly rental does not stop it.
/// </para>
/// </remarks>
[ApiController]
[Authorize]
[Route("api/marketplace/owner")]
public sealed class OwnerRequestsController : MarketplaceControllerBase
{
    /// <summary>Requests received, newest first. Narrow by <c>kind</c>, <c>listingId</c> or <c>status</c>.</summary>
    [HttpGet("requests")]
    [ProducesResponseType<PagedResponse<MarketplaceRequestResponse>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> List(
        [FromServices] IQueryHandler<GetReceivedRequestsQuery, PagedList<MarketplaceRequestView>> handler,
        CancellationToken cancellationToken,
        [FromQuery] string? kind = null,
        [FromQuery] Guid? listingId = null,
        [FromQuery] string? status = null,
        [FromQuery] int? page = null,
        [FromQuery] int? pageSize = null)
    {
        if (!MarketplaceRequestsController.TryParseOptional<RequestKind>(kind, out var parsedKind)
            || !MarketplaceRequestsController.TryParseOptional<RequestStatusFilter>(status, out var parsedStatus))
        {
            return Fail(RequestContractMapper.Invalid("The kind or status is not supported."));
        }

        var result = await handler.Handle(
            new GetReceivedRequestsQuery(parsedKind, listingId, parsedStatus, page, pageSize), cancellationToken);

        return result.IsFailure ? Fail(result.Error!) : Ok(MarketplaceRequestsController.ToPage(result.Value));
    }

    [HttpGet("purchase-requests/{requestId:guid}")]
    [ProducesResponseType<MarketplaceRequestResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status404NotFound)]
    public Task<IActionResult> GetPurchase(
        Guid requestId,
        [FromServices] IQueryHandler<GetReceivedRequestQuery, MarketplaceRequestView> handler,
        CancellationToken cancellationToken) => GetOne(RequestKind.Purchase, requestId, handler, cancellationToken);

    [HttpGet("rental-requests/{requestId:guid}")]
    [ProducesResponseType<MarketplaceRequestResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status404NotFound)]
    public Task<IActionResult> GetRental(
        Guid requestId,
        [FromServices] IQueryHandler<GetReceivedRequestQuery, MarketplaceRequestView> handler,
        CancellationToken cancellationToken) => GetOne(RequestKind.Rental, requestId, handler, cancellationToken);

    [HttpPost("purchase-requests/{requestId:guid}/accept")]
    [ProducesResponseType<AcceptRequestResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status409Conflict)]
    public Task<IActionResult> AcceptPurchase(
        Guid requestId,
        [FromServices] ICommandHandler<AcceptRequestCommand, AcceptRequestResult> handler,
        CancellationToken cancellationToken) => Accept(RequestKind.Purchase, requestId, handler, cancellationToken);

    [HttpPost("rental-requests/{requestId:guid}/accept")]
    [ProducesResponseType<AcceptRequestResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status409Conflict)]
    public Task<IActionResult> AcceptRental(
        Guid requestId,
        [FromServices] ICommandHandler<AcceptRequestCommand, AcceptRequestResult> handler,
        CancellationToken cancellationToken) => Accept(RequestKind.Rental, requestId, handler, cancellationToken);

    [HttpPost("purchase-requests/{requestId:guid}/reject")]
    [ProducesResponseType<RequestDecisionResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status409Conflict)]
    public Task<IActionResult> RejectPurchase(
        Guid requestId,
        [FromServices] ICommandHandler<RejectRequestCommand, RequestDecisionResult> handler,
        CancellationToken cancellationToken) => Reject(RequestKind.Purchase, requestId, handler, cancellationToken);

    [HttpPost("rental-requests/{requestId:guid}/reject")]
    [ProducesResponseType<RequestDecisionResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status409Conflict)]
    public Task<IActionResult> RejectRental(
        Guid requestId,
        [FromServices] ICommandHandler<RejectRequestCommand, RequestDecisionResult> handler,
        CancellationToken cancellationToken) => Reject(RequestKind.Rental, requestId, handler, cancellationToken);

    private async Task<IActionResult> GetOne(
        RequestKind kind,
        Guid requestId,
        IQueryHandler<GetReceivedRequestQuery, MarketplaceRequestView> handler,
        CancellationToken cancellationToken)
    {
        var result = await handler.Handle(new GetReceivedRequestQuery(kind, requestId), cancellationToken);

        return result.IsFailure ? Fail(result.Error!) : Ok(RequestContractMapper.ToResponse(result.Value));
    }

    private async Task<IActionResult> Accept(
        RequestKind kind,
        Guid requestId,
        ICommandHandler<AcceptRequestCommand, AcceptRequestResult> handler,
        CancellationToken cancellationToken)
    {
        var result = await handler.Handle(new AcceptRequestCommand(kind, requestId), cancellationToken);

        return result.IsFailure ? Fail(result.Error!) : Ok(RequestContractMapper.ToResponse(result.Value));
    }

    private async Task<IActionResult> Reject(
        RequestKind kind,
        Guid requestId,
        ICommandHandler<RejectRequestCommand, RequestDecisionResult> handler,
        CancellationToken cancellationToken)
    {
        var result = await handler.Handle(new RejectRequestCommand(kind, requestId), cancellationToken);

        return result.IsFailure ? Fail(result.Error!) : Ok(RequestContractMapper.ToResponse(result.Value));
    }
}
