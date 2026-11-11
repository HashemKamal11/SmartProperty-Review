#nullable enable
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SmartProperty.Api.Contracts;
using SmartProperty.Api.Contracts.Marketplace.Requests;
using SmartProperty.Api.Infrastructure.Authorization;
using SmartProperty.Application.Abstractions.Messaging;
using SmartProperty.Application.Authorization;
using SmartProperty.Application.Marketplace.Requests;
using SmartProperty.Application.Marketplace.Requests.Decide;
using SmartProperty.Application.Marketplace.Requests.Read;
using SmartProperty.Application.Marketplace.Requests.Settings;
using SmartProperty.Common.Pagination;
using SmartProperty.Domain.Marketplace;

namespace SmartProperty.Api.Controllers;

/// <summary>
/// The caller's commitments: accepted purchases and dated rentals, as customer or as owner, and the owner's
/// confirmations that move one forward.
/// </summary>
/// <remarks>
/// <para>
/// A commitment is visible to its customer and its owner only; anyone else gets the same <c>404</c> as for a missing one.
/// An unsigned commitment past its signing deadline is shown as <c>Expired</c> even before anything has stored that.
/// </para>
/// <para>
/// <b>Minimal confirmations, not a lease workflow.</b> The owner records, in order, that the agreement was signed, that
/// the property was handed over, and, for a rental, that it was returned. These are statements by the owner: no document,
/// signature, payment or ownership transfer is involved, and nothing here cancels a signed agreement or handles an
/// overstay. Dates stay blocked while a rental is occupied, until the owner confirms the return.
/// </para>
/// </remarks>
[ApiController]
[Authorize]
[Route("api/marketplace/me/commitments")]
public sealed class MarketplaceCommitmentsController : MarketplaceControllerBase
{
    /// <summary>The caller's commitments, newest first. <c>role</c> is Customer (default) or Owner.</summary>
    [HttpGet]
    [ProducesResponseType<PagedResponse<CommitmentResponse>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> List(
        [FromServices] IQueryHandler<GetMyCommitmentsQuery, PagedList<CommitmentView>> handler,
        CancellationToken cancellationToken,
        [FromQuery] string? role = null,
        [FromQuery] string? status = null,
        [FromQuery] int? page = null,
        [FromQuery] int? pageSize = null)
    {
        CommitmentRole parsedRole = CommitmentRole.Customer;

        if (!string.IsNullOrWhiteSpace(role) && !RequestContractMapper.TryParse(role, out parsedRole))
        {
            return Fail(RequestContractMapper.Invalid("The role must be Customer or Owner."));
        }

        if (!MarketplaceRequestsController.TryParseOptional<CommitmentStatus>(status, out var parsedStatus))
        {
            return Fail(RequestContractMapper.Invalid("The status is not supported."));
        }

        var result = await handler.Handle(new GetMyCommitmentsQuery(parsedRole, parsedStatus, page, pageSize), cancellationToken);

        return result.IsFailure
            ? Fail(result.Error!)
            : Ok(MarketplaceRequestsController.ToPage(result.Value, (CommitmentView view) => RequestContractMapper.ToResponse(view)));
    }

    [HttpGet("{commitmentId:guid}")]
    [ProducesResponseType<CommitmentResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Get(
        Guid commitmentId,
        [FromServices] IQueryHandler<GetMyCommitmentQuery, CommitmentView> handler,
        CancellationToken cancellationToken)
    {
        var result = await handler.Handle(new GetMyCommitmentQuery(commitmentId), cancellationToken);

        return result.IsFailure ? Fail(result.Error!) : Ok(RequestContractMapper.ToResponse(result.Value));
    }

    [HttpPost("{commitmentId:guid}/confirm-signed")]
    [ProducesResponseType<CommitmentStepResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status409Conflict)]
    public Task<IActionResult> ConfirmSigned(
        Guid commitmentId,
        [FromServices] ICommandHandler<ConfirmCommitmentStepCommand, CommitmentStepResult> handler,
        CancellationToken cancellationToken) => Step(commitmentId, CommitmentStep.Signed, handler, cancellationToken);

    [HttpPost("{commitmentId:guid}/confirm-handover")]
    [ProducesResponseType<CommitmentStepResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status409Conflict)]
    public Task<IActionResult> ConfirmHandover(
        Guid commitmentId,
        [FromServices] ICommandHandler<ConfirmCommitmentStepCommand, CommitmentStepResult> handler,
        CancellationToken cancellationToken) => Step(commitmentId, CommitmentStep.Handover, handler, cancellationToken);

    [HttpPost("{commitmentId:guid}/confirm-return")]
    [ProducesResponseType<CommitmentStepResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status409Conflict)]
    public Task<IActionResult> ConfirmReturn(
        Guid commitmentId,
        [FromServices] ICommandHandler<ConfirmCommitmentStepCommand, CommitmentStepResult> handler,
        CancellationToken cancellationToken) => Step(commitmentId, CommitmentStep.Return, handler, cancellationToken);

    private async Task<IActionResult> Step(
        Guid commitmentId,
        CommitmentStep step,
        ICommandHandler<ConfirmCommitmentStepCommand, CommitmentStepResult> handler,
        CancellationToken cancellationToken)
    {
        var result = await handler.Handle(new ConfirmCommitmentStepCommand(commitmentId, step), cancellationToken);

        return result.IsFailure
            ? Fail(result.Error!)
            : Ok(new CommitmentStepResponse(result.Value.CommitmentId, result.Value.Status, result.Value.At));
    }
}

/// <summary>
/// The administrator's control over how long the parties have to sign an accepted request.
/// </summary>
/// <remarks>
/// Requires the Platform-scoped <c>marketplace.settings.manage</c> permission, at the door and again in the handler.
/// The permission is defined but not yet created or assigned by the platform administrator bootstrap, so an
/// administrator must be given it explicitly. A change applies to acceptances made afterwards; a deadline already
/// given to a commitment never moves.
/// </remarks>
[ApiController]
[RequirePermission(PermissionCodes.MarketplaceSettingsManage)]
[Route("api/admin/marketplace/settings/signing-window")]
public sealed class AdminMarketplaceSettingsController : MarketplaceControllerBase
{
    [HttpGet]
    [ProducesResponseType<SigningWindowResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> Get(
        [FromServices] IQueryHandler<GetSigningWindowQuery, SigningWindowResult> handler,
        CancellationToken cancellationToken)
    {
        var result = await handler.Handle(new GetSigningWindowQuery(), cancellationToken);

        return result.IsFailure ? Fail(result.Error!) : Ok(RequestContractMapper.ToResponse(result.Value));
    }

    [HttpPut]
    [ProducesResponseType<SigningWindowResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> Set(
        SetSigningWindowRequest request,
        [FromServices] ICommandHandler<SetSigningWindowCommand, SigningWindowResult> handler,
        CancellationToken cancellationToken)
    {
        if (request.Hours is not { } hours)
        {
            return Fail(RequestContractMapper.Invalid("The number of hours is required."));
        }

        var result = await handler.Handle(new SetSigningWindowCommand(hours), cancellationToken);

        return result.IsFailure ? Fail(result.Error!) : Ok(RequestContractMapper.ToResponse(result.Value));
    }
}
