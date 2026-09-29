#nullable enable
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using SmartProperty.Application.Authorization;

namespace SmartProperty.Api.Infrastructure.Authorization;

/// <summary>
/// Declares that an endpoint requires one platform permission:
/// <c>[RequirePermission(PermissionCodes.WorkspaceAccessRequestsReview)]</c>.
/// </summary>
/// <remarks>
/// One attribute covers both halves of the answer. It derives from <see cref="AuthorizeAttribute"/>, so the
/// authorization middleware applies the default policy first and an unauthenticated caller gets the existing
/// <c>401</c> before any of this runs. It is also an <see cref="IAsyncAuthorizationFilter"/>, which is what
/// carries out the permission check for callers who did authenticate, producing the existing <c>403</c>.
///
/// Being an <see cref="AuthorizeAttribute"/> also puts <see cref="IAuthorizeData"/> into the endpoint's metadata,
/// which is how the OpenAPI operation transformer already decides to emit the Bearer security requirement. A
/// route protected this way documents itself as authenticated without the transformer being touched.
///
/// The check is a translation, not a decision. The requirement, the resource, and the handler are the ones that
/// already exist: it hands the real <see cref="IAuthorizationService"/> a <see cref="PermissionRequirement"/>
/// together with an <see cref="AuthorizationTarget"/> resource, which is the pairing
/// <c>PermissionAuthorizationHandler</c> is typed for, and that handler is the only thing that reaches
/// <c>IPermissionChecker</c>. Nothing here queries the database, reads a role or a claim, or inspects a token,
/// and no controller is left to repeat any of it.
///
/// It fails closed. Only <c>Succeeded</c> allows the action to run; a denial, an unusable identity, and an
/// unrecognized resource all end as <see cref="ForbidResult"/>. A failure inside the checker is deliberately not
/// caught — a database outage must surface as <c>500</c> rather than be disguised as a permission denial.
///
/// <para>
/// Scope is Platform, fixed at construction. Reviewing who may enter a workspace is an act of platform
/// administration, so nothing held inside a workspace may satisfy it. Taking a workspace id from a route and
/// building a workspace target is a separate, still-deferred piece of work (docs/authorization-model.md); until
/// it exists, a workspace-scoped permission must not be declared with this attribute.
/// </para>
/// </remarks>
[AttributeUsage(
    AttributeTargets.Class | AttributeTargets.Method,
    AllowMultiple = true,
    Inherited = true)]
internal sealed class RequirePermissionAttribute : AuthorizeAttribute, IAsyncAuthorizationFilter
{
    private readonly PermissionRequirement _requirement;

    /// <exception cref="ArgumentException">The permission code is null, empty, or whitespace.</exception>
    public RequirePermissionAttribute(string permissionCode)
    {
        // Built once, at attribute construction, so a blank code is a startup-time programming error rather than
        // something a request could discover. The requirement is immutable, which is what makes sharing one
        // instance across every request to the endpoint safe.
        _requirement = new PermissionRequirement(permissionCode);
    }

    /// <summary>The stable <c>Permission.Code</c> this endpoint demands.</summary>
    public string PermissionCode => _requirement.PermissionCode;

    public async Task OnAuthorizationAsync(AuthorizationFilterContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        // An earlier filter already decided the request — a challenge, say. Not second-guessed.
        if (context.Result is not null)
        {
            return;
        }

        var authorizationService = context.HttpContext.RequestServices
            .GetRequiredService<IAuthorizationService>();

        var result = await authorizationService.AuthorizeAsync(
            context.HttpContext.User,
            AuthorizationTarget.Platform,
            [_requirement]);

        if (!result.Succeeded)
        {
            // ForbidResult runs the JwtBearer OnForbidden event, which writes the standard 403 body. It names no
            // permission, role, or policy.
            context.Result = new ForbidResult();
        }
    }
}
