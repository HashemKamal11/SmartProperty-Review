#nullable enable
using Microsoft.AspNetCore.Authorization;
using SmartProperty.Application.Abstractions.Authorization;
using SmartProperty.Application.Abstractions.Identity;
using SmartProperty.Application.Authorization;

namespace SmartProperty.Api.Infrastructure.Authorization;

/// <summary>
/// Bridges ASP.NET Core authorization to <see cref="IPermissionChecker"/>.
/// </summary>
/// <remarks>
/// An adapter, not a decision-maker: it turns the authenticated identity, the requirement's permission code, and
/// the <see cref="AuthorizationTarget"/> resource into one <see cref="AuthorizationRequest"/>, asks the checker,
/// and succeeds the requirement only on <see cref="AuthorizationDecision.Allowed"/>. All permission resolution
/// stays behind the checker; nothing here touches the database, a role, a claim, or a token.
///
/// Typing the resource as <see cref="AuthorizationTarget"/> means the base class never invokes this handler for
/// any other resource — including a null one — so an unexpected resource leaves the requirement unsatisfied
/// instead of reaching a switch that could fall through to success.
///
/// Every negative path simply returns without succeeding the requirement. It does not call
/// <c>context.Fail()</c>: failing outright would block any other handler that might legitimately satisfy the
/// same requirement later, and no public failure reason is wanted — the response is a generic 403 either way.
/// It writes no response at all; the existing JwtBearer <c>OnChallenge</c> and <c>OnForbidden</c> events own the
/// standardized 401 and 403 bodies.
/// </remarks>
internal sealed class PermissionAuthorizationHandler(
    ICurrentUser currentUser,
    IPermissionChecker permissionChecker,
    IHttpContextAccessor httpContextAccessor)
    : AuthorizationHandler<PermissionRequirement, AuthorizationTarget>
{
    protected override async Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        PermissionRequirement requirement,
        AuthorizationTarget resource)
    {
        // Authorization here is request-bound: without a request there is no identity to authorize and no
        // token to cancel with, so fail closed rather than guess.
        if (httpContextAccessor.HttpContext is not { } httpContext)
        {
            return;
        }

        // Authentication has already run. An absent or unusable subject is not something to work around with
        // Guid.Empty or a caller-supplied id — it is a denial.
        if (currentUser.UserId is not { } userId)
        {
            return;
        }

        var request = AuthorizationRequest.For(userId, requirement.PermissionCode, resource);

        // RequestAborted, not CancellationToken.None: an abandoned request must not keep querying.
        // A checker failure or a cancellation propagates — neither is an authorization answer.
        var decision = await permissionChecker.CheckAsync(request, httpContext.RequestAborted);

        if (decision.IsAllowed)
        {
            context.Succeed(requirement);
        }
    }
}
