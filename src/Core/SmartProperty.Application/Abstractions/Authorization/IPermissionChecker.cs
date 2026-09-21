using SmartProperty.Application.Authorization;

namespace SmartProperty.Application.Abstractions.Authorization;

/// <summary>
/// The single authorization boundary: evaluates whether a user holds a permission against a target.
/// </summary>
/// <remarks>
/// Named to avoid collision with ASP.NET Core's <c>IAuthorizationService</c>; this contract knows nothing about
/// HTTP, policies, or principals. No implementation exists yet — resolving permissions against persisted roles
/// is Step 05.6B. See docs/authorization-model.md for the resolution paths and the fail-closed rules an
/// implementation must honour.
/// </remarks>
public interface IPermissionChecker
{
    /// <summary>
    /// Returns <see cref="AuthorizationDecision.Allowed"/> only when the request is satisfied by current
    /// persisted access state. Every negative answer — including missing or non-active users, unknown
    /// permissions, and absent assignments — is <see cref="AuthorizationDecision.Denied"/>.
    /// </summary>
    /// <remarks>
    /// An unexpected persistence failure must propagate rather than be reported as a denial: a database outage
    /// is not an authorization answer.
    /// </remarks>
    Task<AuthorizationDecision> CheckAsync(
        AuthorizationRequest request,
        CancellationToken cancellationToken = default);
}
