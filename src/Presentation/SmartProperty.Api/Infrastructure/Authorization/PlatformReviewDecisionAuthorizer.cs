#nullable enable
using SmartProperty.Application.Abstractions.Authorization;
using SmartProperty.Application.Authorization;

namespace SmartProperty.Api.Infrastructure.Authorization;

/// <summary>
/// Answers <see cref="IReviewDecisionAuthorizer"/> from the one authorization boundary: does this user hold
/// <see cref="PermissionCodes.MarketplaceSubmissionsReviewDecide"/> at Platform scope?
/// </summary>
/// <remarks>
/// An adapter, not a decision-maker, and the mirror of <see cref="PlatformReviewReadAuthorizer"/> for the other
/// permission: it never asks for the read permission. A denial is false; a checker failure propagates untouched, so
/// an outage can never be mistaken for access.
/// </remarks>
internal sealed class PlatformReviewDecisionAuthorizer(IPermissionChecker permissionChecker) : IReviewDecisionAuthorizer
{
    public async Task<bool> IsAuthorizedAsync(Guid reviewerUserId, CancellationToken cancellationToken = default)
    {
        var request = AuthorizationRequest.For(
            reviewerUserId,
            PermissionCodes.MarketplaceSubmissionsReviewDecide,
            AuthorizationTarget.Platform);

        var decision = await permissionChecker.CheckAsync(request, cancellationToken);

        return decision.IsAllowed;
    }
}
