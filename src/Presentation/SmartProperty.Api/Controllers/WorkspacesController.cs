#nullable enable
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SmartProperty.Api.Contracts.Workspaces;
using SmartProperty.Api.Infrastructure.Errors;
using SmartProperty.Application.Abstractions.Messaging;
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
}
