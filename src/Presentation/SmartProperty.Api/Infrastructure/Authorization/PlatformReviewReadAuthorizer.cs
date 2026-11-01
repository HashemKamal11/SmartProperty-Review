#nullable enable
using SmartProperty.Application.Abstractions.Authorization;
using SmartProperty.Application.Authorization;

namespace SmartProperty.Api.Infrastructure.Authorization;

/// <summary>
/// Answers <see cref="IReviewReadAuthorizer"/> from the one authorization boundary,
/// <see cref="IPermissionChecker"/>: does this user hold <see cref="PermissionCodes.MarketplaceSubmissionsReviewRead"/>
/// at Platform scope?
/// </summary>
/// <remarks>
/// An adapter, not a decision-maker. It builds one <see cref="AuthorizationRequest"/> for the reviewer id it was
/// given, asks the checker, and returns <see langword="true"/> only for <see cref="AuthorizationDecision.Allowed"/>.
/// Persisted role and permission resolution, including the requirement that the user is active, stays behind the
/// checker; nothing here repeats its joins, reads a role name, a Workspace membership or a token claim, or consults
/// any other permission. In particular it never asks for
/// <see cref="PermissionCodes.MarketplaceSubmissionsReviewDecide"/>: the authority to read the queue and the
/// authority to approve or reject are independent.
///
/// It fails closed. A denial is <see langword="false"/>; a checker failure, including cancellation, is not an
/// answer and propagates untouched, so an outage can never be mistaken for access. A blank reviewer id is rejected
/// by <see cref="AuthorizationRequest.For"/> rather than papered over.
///
/// This type is not registered. Making the review read handlers resolvable, and exposing them, are separate steps
/// that need their own approval.
/// </remarks>
internal sealed class PlatformReviewReadAuthorizer(IPermissionChecker permissionChecker) : IReviewReadAuthorizer
{
    public async Task<bool> IsAuthorizedAsync(Guid reviewerUserId, CancellationToken cancellationToken = default)
    {
        var request = AuthorizationRequest.For(
            reviewerUserId,
            PermissionCodes.MarketplaceSubmissionsReviewRead,
            AuthorizationTarget.Platform);

        var decision = await permissionChecker.CheckAsync(request, cancellationToken);

        return decision.IsAllowed;
    }
}
