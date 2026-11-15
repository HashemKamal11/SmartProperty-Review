#nullable enable
using Microsoft.AspNetCore.Mvc;
using SmartProperty.Api.Contracts;
using SmartProperty.Api.Contracts.Workspaces;
using SmartProperty.Api.Infrastructure.Authorization;
using SmartProperty.Api.Infrastructure.Errors;
using SmartProperty.Application.Abstractions.Messaging;
using SmartProperty.Application.Authorization;
using SmartProperty.Application.WorkspaceRegistrations;
using SmartProperty.Application.WorkspaceRegistrations.Review;
using SmartProperty.Common.Pagination;
using SmartProperty.Domain.Workspaces;

namespace SmartProperty.Api.Controllers;

/// <summary>
/// Platform administration of company workspace registrations: the queue, one request, approve (creates the workspace
/// and makes the applicant its admin) and reject (with a reason). The reviewer is always the authenticated caller.
/// </summary>
[ApiController]
[Route("api/admin/workspace-registrations")]
[RequirePermission(PermissionCodes.WorkspaceRegistrationsReview)]
public sealed class AdminWorkspaceRegistrationsController : ControllerBase
{
    /// <summary>The review queue, oldest first. <paramref name="status"/> defaults to <c>Pending</c>.</summary>
    [HttpGet]
    [ProducesResponseType<PagedResponse<WorkspaceRegistrationResponse>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> List(
        [FromServices] IQueryHandler<GetWorkspaceRegistrationsQuery, PagedList<WorkspaceRegistrationView>> handler,
        CancellationToken cancellationToken,
        [FromQuery] WorkspaceRegistrationRequestStatus? status = null,
        [FromQuery] int? page = null,
        [FromQuery] int? pageSize = null)
    {
        var result = await handler.Handle(new GetWorkspaceRegistrationsQuery(status, page, pageSize), cancellationToken);

        if (result.IsFailure)
        {
            var error = ApiErrorResponseFactory.FromError(result.Error!, HttpContext);
            return StatusCode(error.Status, error);
        }

        var data = result.Value;
        return Ok(PagedResponse<WorkspaceRegistrationResponse>.FromPagedList(
            PagedList<WorkspaceRegistrationResponse>.Create(
                data.Items.Select(WorkspaceRegistrationResponse.From).ToArray(),
                data.PageNumber,
                data.PageSize,
                data.TotalCount)));
    }

    [HttpGet("{id:guid}")]
    [ProducesResponseType<WorkspaceRegistrationResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetById(
        Guid id,
        [FromServices] IQueryHandler<GetWorkspaceRegistrationQuery, WorkspaceRegistrationView> handler,
        CancellationToken cancellationToken)
    {
        var result = await handler.Handle(new GetWorkspaceRegistrationQuery(id), cancellationToken);

        if (result.IsFailure)
        {
            var error = ApiErrorResponseFactory.FromError(result.Error!, HttpContext);
            return StatusCode(error.Status, error);
        }

        return Ok(WorkspaceRegistrationResponse.From(result.Value));
    }

    /// <summary>
    /// Approves a pending request: creates the workspace named after the company and makes the applicant its Workspace
    /// Admin. 409 when it was already decided or the applicant has not verified the phone number and email yet.
    /// </summary>
    [HttpPost("{id:guid}/approve")]
    [ProducesResponseType<ApproveWorkspaceRegistrationResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Approve(
        Guid id,
        [FromServices] ICommandHandler<ApproveWorkspaceRegistrationCommand, ApproveWorkspaceRegistrationResult> handler,
        CancellationToken cancellationToken)
    {
        var result = await handler.Handle(new ApproveWorkspaceRegistrationCommand(id), cancellationToken);

        if (result.IsFailure)
        {
            var error = ApiErrorResponseFactory.FromError(result.Error!, HttpContext);
            return StatusCode(error.Status, error);
        }

        var approval = result.Value;
        return Ok(new ApproveWorkspaceRegistrationResponse(
            approval.RequestId,
            approval.Status.ToString(),
            approval.WorkspaceId,
            approval.WorkspaceName,
            approval.ApplicantUserId,
            approval.MembershipId));
    }

    /// <summary>Rejects a pending request with a reason the applicant can read. Nothing else changes.</summary>
    [HttpPost("{id:guid}/reject")]
    [ProducesResponseType<WorkspaceRegistrationResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> Reject(
        Guid id,
        RejectWorkspaceRegistrationRequest request,
        [FromServices] ICommandHandler<RejectWorkspaceRegistrationCommand, WorkspaceRegistrationView> handler,
        CancellationToken cancellationToken)
    {
        var result = await handler.Handle(new RejectWorkspaceRegistrationCommand(id, request.Reason), cancellationToken);

        if (result.IsFailure)
        {
            var error = ApiErrorResponseFactory.FromError(result.Error!, HttpContext);
            return StatusCode(error.Status, error);
        }

        return Ok(WorkspaceRegistrationResponse.From(result.Value));
    }
}
