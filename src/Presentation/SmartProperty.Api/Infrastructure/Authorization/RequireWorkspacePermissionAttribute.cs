#nullable enable
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using SmartProperty.Application.Authorization;

namespace SmartProperty.Api.Infrastructure.Authorization;

/// <summary>
/// Declares that an endpoint requires one workspace permission, in the workspace named by the route:
/// <c>[RequireWorkspacePermission(PermissionCodes.WorkspaceRead)]</c> on a <c>{workspaceId:guid}</c> route.
/// </summary>
/// <remarks>
/// The workspace counterpart of <see cref="RequirePermissionAttribute"/>, in the same shape: an
/// <see cref="AuthorizeAttribute"/> (so the default policy answers <c>401</c> first and the OpenAPI transformer
/// documents the route as authenticated) and an <see cref="IAsyncAuthorizationFilter"/> that produces the
/// <c>403</c>. The only difference is the target. It reads <c>workspaceId</c> from the route values and builds
/// <see cref="AuthorizationTarget.Workspace(Guid)"/>; the requirement, the service, the handler, and
/// <c>IPermissionChecker</c> are the existing ones, so no checking logic is repeated here.
///
/// It fails closed, with one response for everything: a missing, empty, or unparseable <c>workspaceId</c>, a
/// workspace that does not exist, a caller who is not a member, and a member without the permission all end as
/// <see cref="ForbidResult"/>. Nothing distinguishes them, so the response does not reveal whether a workspace
/// exists or whether the caller belongs to it. A platform role never satisfies this target, and platform
/// administration grants no implicit membership. A failure inside the checker is deliberately not caught.
/// </remarks>
[AttributeUsage(
    AttributeTargets.Class | AttributeTargets.Method,
    AllowMultiple = true,
    Inherited = true)]
internal sealed class RequireWorkspacePermissionAttribute : AuthorizeAttribute, IAsyncAuthorizationFilter
{
    public const string WorkspaceIdRouteKey = "workspaceId";

    private readonly PermissionRequirement _requirement;

    /// <exception cref="ArgumentException">The permission code is null, empty, or whitespace.</exception>
    public RequireWorkspacePermissionAttribute(string permissionCode)
    {
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

        if (!TryGetWorkspaceId(context, out var workspaceId))
        {
            context.Result = new ForbidResult();
            return;
        }

        var authorizationService = context.HttpContext.RequestServices
            .GetRequiredService<IAuthorizationService>();

        var result = await authorizationService.AuthorizeAsync(
            context.HttpContext.User,
            AuthorizationTarget.Workspace(workspaceId),
            [_requirement]);

        if (!result.Succeeded)
        {
            context.Result = new ForbidResult();
        }
    }

    private static bool TryGetWorkspaceId(AuthorizationFilterContext context, out Guid workspaceId)
    {
        workspaceId = Guid.Empty;

        return context.RouteData.Values.TryGetValue(WorkspaceIdRouteKey, out var raw)
            && Guid.TryParse(Convert.ToString(raw, System.Globalization.CultureInfo.InvariantCulture), out workspaceId)
            && workspaceId != Guid.Empty;
    }
}
