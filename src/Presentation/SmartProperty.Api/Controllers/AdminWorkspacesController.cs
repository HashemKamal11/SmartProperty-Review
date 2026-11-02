#nullable enable
using Microsoft.AspNetCore.Mvc;
using SmartProperty.Api.Contracts;
using SmartProperty.Api.Contracts.Workspaces;
using SmartProperty.Api.Infrastructure.Authorization;
using SmartProperty.Api.Infrastructure.Errors;
using SmartProperty.Application.Abstractions.Messaging;
using SmartProperty.Application.Authorization;
using SmartProperty.Application.Workspaces.Administration;
using SmartProperty.Application.Workspaces.Administration.Create;
using SmartProperty.Application.Workspaces.Administration.Get;
using SmartProperty.Application.Workspaces.Administration.List;
using SmartProperty.Common.Pagination;

namespace SmartProperty.Api.Controllers;

/// <summary>Platform administration; these permissions never imply Workspace membership.</summary>
[ApiController]
[Route("api/admin/workspaces")]
public sealed class AdminWorkspacesController : ControllerBase
{
    [HttpGet]
    [RequirePermission(PermissionCodes.WorkspaceAdminRead)]
    [ProducesResponseType<PagedResponse<AdminWorkspaceResponse>>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> List(
        [FromServices] IQueryHandler<GetAdminWorkspacesQuery, PagedList<AdminWorkspaceDetails>> handler,
        CancellationToken cancellationToken,
        [FromQuery] int? page = null,
        [FromQuery] int? pageSize = null)
    {
        var result = await handler.Handle(new(page, pageSize), cancellationToken);
        if (result.IsFailure)
        {
            var error = ApiErrorResponseFactory.FromError(result.Error!, HttpContext);
            return StatusCode(error.Status, error);
        }
        var data = result.Value;
        return Ok(PagedResponse<AdminWorkspaceResponse>.FromPagedList(
            PagedList<AdminWorkspaceResponse>.Create(data.Items.Select(Map).ToArray(), data.PageNumber, data.PageSize, data.TotalCount)));
    }

    [HttpGet("{workspaceId:guid}")]
    [RequirePermission(PermissionCodes.WorkspaceAdminRead)]
    [ProducesResponseType<AdminWorkspaceResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetById(Guid workspaceId,
        [FromServices] IQueryHandler<GetAdminWorkspaceQuery, AdminWorkspaceDetails> handler,
        CancellationToken cancellationToken)
    {
        var result = await handler.Handle(new(workspaceId), cancellationToken);
        if (result.IsFailure)
        {
            var error = ApiErrorResponseFactory.FromError(result.Error!, HttpContext);
            return StatusCode(error.Status, error);
        }
        return Ok(Map(result.Value));
    }

    [HttpPost]
    [RequirePermission(PermissionCodes.WorkspaceAdminCreate)]
    [ProducesResponseType<AdminWorkspaceResponse>(StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> Create(CreateWorkspaceRequest request,
        [FromServices] ICommandHandler<CreateWorkspaceCommand, AdminWorkspaceDetails> handler,
        CancellationToken cancellationToken)
    {
        var result = await handler.Handle(new(request.Name), cancellationToken);
        if (result.IsFailure)
        {
            var error = ApiErrorResponseFactory.FromError(result.Error!, HttpContext);
            return StatusCode(error.Status, error);
        }
        return CreatedAtAction(nameof(GetById), new { workspaceId = result.Value.Id }, Map(result.Value));
    }

    private static AdminWorkspaceResponse Map(AdminWorkspaceDetails workspace) =>
        new(workspace.Id, workspace.Name, workspace.CreatedAt, workspace.UpdatedAt);
}