#nullable enable
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SmartProperty.Api.Contracts.Workspaces;
using SmartProperty.Api.Infrastructure.Authorization;
using SmartProperty.Api.Infrastructure.Errors;
using SmartProperty.Application.Abstractions.Messaging;
using SmartProperty.Application.Authorization;
using SmartProperty.Application.Workspaces.Get;
using SmartProperty.Application.Workspaces.RegistrationOptions;

namespace SmartProperty.Api.Controllers;

[ApiController]
[Route("api/workspaces")]
public sealed class WorkspacesController : ControllerBase
{
    /// <summary>
    /// The workspaces a prospective user may choose when registering.
    /// </summary>
    /// <remarks>
    /// Anonymous because it is read before an account exists: registration requires a workspace id, and this is
    /// how a caller discovers one. Returns an empty array when no workspace exists.
    /// </remarks>
    [AllowAnonymous]
    [HttpGet("registration-options")]
    [ProducesResponseType<IReadOnlyList<RegistrationWorkspaceOptionResponse>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> GetRegistrationOptions(
        [FromServices] IQueryHandler<GetRegistrationWorkspaceOptionsQuery, IReadOnlyList<RegistrationWorkspaceOption>> handler,
        CancellationToken cancellationToken)
    {
        var result = await handler.Handle(new GetRegistrationWorkspaceOptionsQuery(), cancellationToken);

        if (result.IsFailure)
        {
            var errorResponse = ApiErrorResponseFactory.FromError(result.Error!, HttpContext);

            return StatusCode(errorResponse.Status, errorResponse);
        }

        var response = result.Value
            .Select(option => new RegistrationWorkspaceOptionResponse(option.Id, option.Name))
            .ToArray();

        return Ok(response);
    }

    /// <summary>
    /// One workspace's id and name, for a member holding <c>workspace.read</c> in that workspace.
    /// </summary>
    /// <remarks>
    /// A caller who is not a member, a member without the permission, and a workspace that does not exist are all
    /// the same <c>403</c>: the response never reveals whether a workspace exists. Only a value that is not a guid
    /// is a <c>404</c>, by the route constraint.
    /// </remarks>
    [HttpGet("{workspaceId:guid}")]
    [RequireWorkspacePermission(PermissionCodes.WorkspaceRead)]
    [ProducesResponseType<WorkspaceResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetById(
        Guid workspaceId,
        [FromServices] IQueryHandler<GetWorkspaceQuery, WorkspaceDetails> handler,
        CancellationToken cancellationToken)
    {
        var result = await handler.Handle(new GetWorkspaceQuery(workspaceId), cancellationToken);

        // The authorization filter has already answered for anyone who may not read. A workspace that vanished
        // between that check and this read is the same 403, never a 404.
        if (result.IsFailure)
        {
            return Forbid();
        }

        return Ok(new WorkspaceResponse(result.Value.Id, result.Value.Name));
    }
}
