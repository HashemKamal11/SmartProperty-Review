#nullable enable
using Microsoft.AspNetCore.Mvc;
using SmartProperty.Api.Contracts;
using SmartProperty.Api.Contracts.Administration;
using SmartProperty.Api.Infrastructure.Authorization;
using SmartProperty.Api.Infrastructure.Errors;
using SmartProperty.Application.Abstractions.Messaging;
using SmartProperty.Application.Authorization;
using SmartProperty.Application.WorkspaceAccessRequests.Approve;
using SmartProperty.Application.WorkspaceAccessRequests.List;
using SmartProperty.Application.WorkspaceAccessRequests.Reject;
using SmartProperty.Common.Pagination;
using SmartProperty.Domain.Workspaces;

namespace SmartProperty.Api.Controllers;

/// <summary>
/// Platform administration of workspace access requests: see what is waiting, and decide it.
/// </summary>
/// <remarks>
/// The permission is declared once, on the controller, so every action inherits it and none can be added without
/// it. Authentication and the permission check both come from that one attribute; there is no authorization logic
/// in any action below, and nothing here touches <c>IPermissionChecker</c>, a role, or a claim.
///
/// The actions are deliberately thin: bind, hand one command or query to its handler, map the result. The reviewer
/// is never a parameter — the use cases read it from the authenticated identity.
/// </remarks>
[ApiController]
[Route("api/admin/workspace-access-requests")]
[RequirePermission(PermissionCodes.WorkspaceAccessRequestsReview)]
public sealed class AdminWorkspaceAccessRequestsController : ControllerBase
{
    /// <summary>
    /// The review queue. Without this, approve and reject would be unusable: nothing else reveals a request id.
    /// </summary>
    /// <remarks>
    /// <paramref name="status"/> defaults to <c>Pending</c>, which is the queue a reviewer wants; an explicit
    /// value reads the others. An unparseable status or workspace id is a request-shape failure and is answered by
    /// the standard <c>400 request.malformed</c> that <c>ApiBehaviorOptions</c> already produces.
    /// </remarks>
    [HttpGet]
    public async Task<IActionResult> List(
        [FromServices] IQueryHandler<GetWorkspaceAccessRequestsQuery, PagedList<WorkspaceAccessRequestListItem>> handler,
        CancellationToken cancellationToken,
        [FromQuery] Guid? workspaceId = null,
        [FromQuery] WorkspaceAccessRequestStatus? status = null,
        [FromQuery] int? page = null,
        [FromQuery] int? pageSize = null)
    {
        var query = new GetWorkspaceAccessRequestsQuery(workspaceId, status, page, pageSize);

        var result = await handler.Handle(query, cancellationToken);

        if (result.IsFailure)
        {
            var errorResponse = ApiErrorResponseFactory.FromError(result.Error!, HttpContext);

            return StatusCode(errorResponse.Status, errorResponse);
        }

        var pagedList = result.Value;
        var items = pagedList.Items
            .Select(item => new WorkspaceAccessRequestResponse(
                item.RequestId,
                item.UserId,
                item.UserEmail,
                item.UserFirstName,
                item.UserLastName,
                item.WorkspaceId,
                item.WorkspaceName,
                item.Status.ToString(),
                item.RequestedAt,
                item.ReviewedAt,
                item.ReviewedByUserId))
            .ToArray();

        // Re-paged rather than hand-assembled, so the response's page metadata comes from the same
        // PagedList/PagedResponse pair every other collection endpoint will use.
        return Ok(PagedResponse<WorkspaceAccessRequestResponse>.FromPagedList(
            PagedList<WorkspaceAccessRequestResponse>.Create(
                items,
                pagedList.PageNumber,
                pagedList.PageSize,
                pagedList.TotalCount)));
    }

    /// <summary>
    /// Approves a pending request: records the decision, activates a Pending account, and creates the workspace
    /// membership. Assigns no role.
    /// </summary>
    /// <remarks>
    /// Returns <c>200</c> with the resulting state rather than <c>204</c>: the membership id and the two resulting
    /// statuses are the useful outcome of the call, and a caller should not have to go looking for them.
    /// </remarks>
    [HttpPost("{id:guid}/approve")]
    public async Task<IActionResult> Approve(
        Guid id,
        [FromServices] ICommandHandler<ApproveWorkspaceAccessRequestCommand, ApproveWorkspaceAccessRequestResult> handler,
        CancellationToken cancellationToken)
    {
        var result = await handler.Handle(new ApproveWorkspaceAccessRequestCommand(id), cancellationToken);

        if (result.IsFailure)
        {
            var errorResponse = ApiErrorResponseFactory.FromError(result.Error!, HttpContext);

            return StatusCode(errorResponse.Status, errorResponse);
        }

        var approval = result.Value;

        return Ok(new ApproveWorkspaceAccessRequestResponse(
            approval.RequestId,
            approval.UserId,
            approval.WorkspaceId,
            approval.RequestStatus.ToString(),
            approval.UserStatus.ToString(),
            approval.MembershipId));
    }

    /// <summary>
    /// Rejects a pending request. Records the decision and nothing else: no activation, no membership, no role.
    /// </summary>
    [HttpPost("{id:guid}/reject")]
    public async Task<IActionResult> Reject(
        Guid id,
        [FromServices] ICommandHandler<RejectWorkspaceAccessRequestCommand, RejectWorkspaceAccessRequestResult> handler,
        CancellationToken cancellationToken)
    {
        var result = await handler.Handle(new RejectWorkspaceAccessRequestCommand(id), cancellationToken);

        if (result.IsFailure)
        {
            var errorResponse = ApiErrorResponseFactory.FromError(result.Error!, HttpContext);

            return StatusCode(errorResponse.Status, errorResponse);
        }

        var rejection = result.Value;

        return Ok(new RejectWorkspaceAccessRequestResponse(
            rejection.RequestId,
            rejection.UserId,
            rejection.WorkspaceId,
            rejection.RequestStatus.ToString(),
            rejection.UserStatus.ToString()));
    }
}
