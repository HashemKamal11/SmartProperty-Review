#nullable enable
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SmartProperty.Api.Contracts.Workspaces;
using SmartProperty.Api.Infrastructure.Errors;
using SmartProperty.Application.Abstractions.Messaging;
using SmartProperty.Application.WorkspaceRegistrations;
using SmartProperty.Application.WorkspaceRegistrations.Apply;
using SmartProperty.Application.WorkspaceRegistrations.Mine;

namespace SmartProperty.Api.Controllers;

/// <summary>
/// The applicant's side of workspace registration: an existing signed-in account applies for a workspace for its
/// company, and any applicant follows its requests. New companies sign up with <c>POST /api/auth/register-workspace</c>.
/// </summary>
[ApiController]
[Route("api/workspace-registrations")]
[Authorize]
public sealed class WorkspaceRegistrationsController : ControllerBase
{
    [HttpPost]
    [ProducesResponseType<WorkspaceRegistrationResponse>(StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> Apply(
        ApplyForWorkspaceRequest request,
        [FromServices] ICommandHandler<ApplyForWorkspaceCommand, WorkspaceRegistrationView> handler,
        CancellationToken cancellationToken)
    {
        var result = await handler.Handle(
            new ApplyForWorkspaceCommand(request.CompanyName, request.CommercialRegistrationNumber),
            cancellationToken);

        if (result.IsFailure)
        {
            var error = ApiErrorResponseFactory.FromError(result.Error!, HttpContext);
            return StatusCode(error.Status, error);
        }

        return StatusCode(StatusCodes.Status201Created, WorkspaceRegistrationResponse.From(result.Value));
    }

    /// <summary>The caller's own requests, newest first, with the status and any rejection reason.</summary>
    [HttpGet("mine")]
    [ProducesResponseType<IReadOnlyList<WorkspaceRegistrationResponse>>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> Mine(
        [FromServices] IQueryHandler<GetMyWorkspaceRegistrationsQuery, IReadOnlyList<WorkspaceRegistrationView>> handler,
        CancellationToken cancellationToken)
    {
        var result = await handler.Handle(new GetMyWorkspaceRegistrationsQuery(), cancellationToken);

        if (result.IsFailure)
        {
            var error = ApiErrorResponseFactory.FromError(result.Error!, HttpContext);
            return StatusCode(error.Status, error);
        }

        return Ok(result.Value.Select(WorkspaceRegistrationResponse.From).ToArray());
    }
}
