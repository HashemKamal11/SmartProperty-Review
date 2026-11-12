#nullable enable
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SmartProperty.Api.Contracts;
using SmartProperty.Api.Infrastructure.Authorization;
using SmartProperty.Api.Infrastructure.Errors;
using SmartProperty.Application.Authorization;
using SmartProperty.Application.Identity.StaffRoles;
using SmartProperty.Common.Results;

namespace SmartProperty.Api.Controllers;

public sealed record AssignStaffRequest(string? Email);

/// <summary>
/// Platform staff administration: who is a Legal Reviewer. Needs <c>platform.staff.manage</c>, which the Platform Admin
/// role holds. Nobody can change their own roles.
/// </summary>
[ApiController]
[Authorize]
[RequirePermission(PermissionCodes.PlatformStaffManage)]
[Route("api/admin/staff/legal-reviewers")]
public sealed class AdminStaffController(LegalReviewerAdministration staff) : ControllerBase
{
    [HttpGet]
    [ProducesResponseType<IReadOnlyList<StaffMemberView>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> List(CancellationToken cancellationToken) => ToResult(await staff.ListAsync(cancellationToken));

    /// <summary>Gives an Active account with a verified email the Legal Reviewer role.</summary>
    [HttpPost]
    [ProducesResponseType<StaffMemberView>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Assign(AssignStaffRequest request, CancellationToken cancellationToken) =>
        ToResult(await staff.AssignAsync(request.Email, cancellationToken));

    [HttpDelete("{userId:guid}")]
    [ProducesResponseType<StaffMemberView>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Remove(Guid userId, CancellationToken cancellationToken) =>
        ToResult(await staff.RemoveAsync(userId, cancellationToken));

    private IActionResult ToResult<T>(Result<T> result)
    {
        if (!result.IsFailure)
        {
            return Ok(result.Value);
        }

        var response = ApiErrorResponseFactory.FromError(result.Error!, HttpContext);

        return StatusCode(response.Status, response);
    }
}
