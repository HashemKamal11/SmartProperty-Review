#nullable enable
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SmartProperty.Api.Contracts;
using SmartProperty.Api.Contracts.Workspaces;
using SmartProperty.Api.Infrastructure.Errors;
using SmartProperty.Application.Abstractions.Identity;
using SmartProperty.Application.Abstractions.Messaging;
using SmartProperty.Application.Workspaces.Members.Create;
using SmartProperty.Common.Results;

namespace SmartProperty.Api.Controllers;

/// <summary>Member accounts of the caller's own workspace, created by that workspace's administrator.</summary>
[ApiController]
[Route("api/workspaces/members")]
public sealed class WorkspaceMembersController : ControllerBase
{
    /// <summary>
    /// Creates an Active account and makes it a member of the caller's workspace with the default Member role.
    /// </summary>
    /// <remarks>
    /// The request names no workspace: it is the one in which the signed-in caller holds
    /// <c>workspace.members.create</c>, so an administrator can only add members to their own workspace and a body
    /// cannot name another. A caller who administers no workspace gets 403. The new account can sign in at once.
    /// </remarks>
    [HttpPost]
    [Authorize]
    [ProducesResponseType<WorkspaceMemberResponse>(StatusCodes.Status201Created)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> Create(
        CreateWorkspaceMemberRequest request,
        [FromServices] ICurrentUser currentUser,
        [FromServices] ICommandHandler<CreateWorkspaceMemberCommand, CreateWorkspaceMemberResult> handler,
        CancellationToken cancellationToken)
    {
        // The token's subject is the only identity that counts.
        if (currentUser.UserId is not { } adminUserId)
        {
            return Unauthorized(ApiErrorResponseFactory.Unauthorized(HttpContext));
        }

        var result = await handler.Handle(
            new CreateWorkspaceMemberCommand(adminUserId, request.Email, request.Password, request.FirstName, request.LastName),
            cancellationToken);

        if (result.IsFailure)
        {
            // Not an administrator: the standard 403, the same as every other permission failure.
            var error = result.Error!.Type == ErrorType.Forbidden
                ? ApiErrorResponseFactory.Forbidden(HttpContext)
                : ApiErrorResponseFactory.FromError(result.Error, HttpContext);
            return StatusCode(error.Status, error);
        }

        var member = result.Value;

        // No endpoint reads a single member back yet, so the response has no Location header.
        return StatusCode(
            StatusCodes.Status201Created,
            new WorkspaceMemberResponse(
                member.UserId, member.MembershipId, member.WorkspaceId, member.Email,
                member.FirstName, member.LastName, member.RoleName, member.CreatedAt));
    }
}
