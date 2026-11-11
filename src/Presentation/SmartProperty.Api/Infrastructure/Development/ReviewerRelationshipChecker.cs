#nullable enable
using Microsoft.EntityFrameworkCore;
using SmartProperty.Application.Abstractions.Marketplace;
using SmartProperty.Domain.Identity;
using SmartProperty.Domain.Marketplace;
using SmartProperty.Persistence.Context;

namespace SmartProperty.Api.Infrastructure.Development;

/// <summary>
/// Works out, from persisted data, whether a person is independent of a submission: not its submitter, not its declared
/// owner, and not tied to either through a workspace.
/// </summary>
/// <remarks>
/// <para>
/// <b>Different account ids are not enough.</b> The reviewer is compared with the submitter and with the declared owner's
/// linked account, and then with everyone those two sit beside: a reviewer who shares <b>any workspace</b> with the
/// submitter or the owner is treated as having a conflict, and a reviewer who is a member of the submitting workspace is
/// affiliated with it.
/// </para>
/// <para>
/// <b>What it cannot know is "unknown", not "fine".</b> An organisation owner, or an owner who is not linked to an account,
/// has controlling persons nobody can enumerate here, so the conflict answer is unknown (null) and the decision is refused.
/// </para>
/// <para>
/// <b>Simulated.</b> There is no identity-verification service. "Identity verified" here means only that the account is
/// <c>Active</c>; that is a simulation and every use of it is labelled.
/// </para>
/// </remarks>
internal sealed class ReviewerRelationshipChecker(ApplicationDbContext context)
{
    public const string SimulationLabel =
        "SIMULATED: no identity-verification service exists, so 'identity verified' means only an Active account.";

    public async Task<ReviewerIndependenceFacts> CheckAsync(
        Guid personUserId,
        Guid submitterUserId,
        ListingOwnerIdentity declaredOwner,
        Guid? submittingWorkspaceId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(declaredOwner);

        var status = await context.Users.AsNoTracking()
            .Where(user => user.Id == personUserId)
            .Select(user => (UserStatus?)user.Status)
            .SingleOrDefaultAsync(cancellationToken);
        var active = status == UserStatus.Active;

        bool? conflict;

        if (personUserId == submitterUserId || personUserId == declaredOwner.LinkedUserId)
        {
            conflict = true;
        }
        else if (declaredOwner.Kind != ListingOwnerKind.Individual || declaredOwner.LinkedUserId is not { } ownerUser)
        {
            // Controlling persons of an organisation, or of an owner with no account, cannot be listed here.
            conflict = null;
        }
        else
        {
            var people = new[] { submitterUserId, ownerUser };
            var theirWorkspaces = context.WorkspaceMemberships.AsNoTracking()
                .Where(membership => people.Contains(membership.UserId))
                .Select(membership => membership.WorkspaceId);
            conflict = await context.WorkspaceMemberships.AsNoTracking()
                .AnyAsync(membership => membership.UserId == personUserId && theirWorkspaces.Contains(membership.WorkspaceId), cancellationToken);
        }

        bool? affiliated = submittingWorkspaceId is { } workspace
            ? await context.WorkspaceMemberships.AsNoTracking()
                .AnyAsync(membership => membership.UserId == personUserId && membership.WorkspaceId == workspace, cancellationToken)
            : null;

        return new ReviewerIndependenceFacts(active, conflict, affiliated);
    }
}

/// <summary>
/// The Development-only reviewer-independence source: the real relationship check above, with the simulated identity
/// verification labelled. Registered only in demo mode.
/// </summary>
internal sealed class RelationshipReviewerIndependenceProvider(
    ReviewerRelationshipChecker checker,
    ILogger<RelationshipReviewerIndependenceProvider> logger) : IReviewerIndependenceProvider
{
    public async Task<ReviewerIndependenceFacts> GetAsync(
        Guid reviewerUserId,
        Guid submitterUserId,
        ListingOwnerIdentity declaredOwner,
        Guid? submittingWorkspaceId,
        CancellationToken cancellationToken = default)
    {
        var facts = await checker.CheckAsync(reviewerUserId, submitterUserId, declaredOwner, submittingWorkspaceId, cancellationToken);

        logger.LogWarning(
            "DEMO MODE reviewer independence: conflict={Conflict}, affiliated={Affiliated}. {Label}",
            facts.ReviewerHasOwnerConflict,
            facts.ReviewerAffiliatedWithWorkspace,
            ReviewerRelationshipChecker.SimulationLabel);

        return facts;
    }
}
