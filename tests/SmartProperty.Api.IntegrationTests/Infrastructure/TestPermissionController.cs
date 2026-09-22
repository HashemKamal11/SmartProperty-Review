using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SmartProperty.Api.Infrastructure.Authorization;
using SmartProperty.Application.Authorization;

namespace SmartProperty.Api.IntegrationTests.Infrastructure;

/// <summary>
/// A route that exists only inside the test host, so the permission bridge can be exercised end to end over
/// real HTTP while no production endpoint applies a <see cref="PermissionRequirement"/> yet.
/// </summary>
/// <remarks>
/// It lives in the test assembly and reaches the host only through <c>AddApplicationPart</c> in
/// <see cref="SmartPropertyApiFactory"/>. Nothing in <c>SmartProperty.Api</c> references it, and it is not
/// reachable from the published application.
///
/// It deliberately adds no authorization logic of its own: it builds one requirement, hands the real
/// <see cref="IAuthorizationService"/> the <see cref="AuthorizationTarget"/> resource, and turns the answer
/// into <c>Ok</c> or <c>Forbid</c> so the standard 401/403 responses come from the production pipeline.
/// </remarks>
[ApiController]
[Route("__tests__/authorization")]
public sealed class TestPermissionController : ControllerBase
{
    /// <summary>The permission code the tests use when the code itself is not what is under test.</summary>
    internal const string DefaultPermissionCode = "property.read";

    [Authorize]
    [HttpGet("platform")]
    public async Task<IActionResult> RequirePlatformPermission(
        [FromServices] IAuthorizationService authorizationService,
        [FromQuery] string? permission)
    {
        var requirement = new PermissionRequirement(permission ?? DefaultPermissionCode);

        var result = await authorizationService.AuthorizeAsync(
            User,
            AuthorizationTarget.Platform,
            [requirement]);

        // Forbid() runs the JwtBearer OnForbidden event, which writes the standard ApiErrorResponse.
        return result.Succeeded ? Ok(new TestPermissionResponse(Allowed: true)) : Forbid();
    }
}

public sealed record TestPermissionResponse(bool Allowed);
