#nullable enable
using Microsoft.EntityFrameworkCore;
using SmartProperty.Application.Abstractions.Marketplace;
using SmartProperty.Domain.Identity;
using SmartProperty.Domain.Marketplace;
using SmartProperty.Domain.OwnerVerification;
using SmartProperty.Persistence.Context;

namespace SmartProperty.Api.Infrastructure.Marketplace;

/// <summary>
/// The production source of reviewer-independence facts: what the platform's own verified records can establish.
/// Nothing is simulated and nothing is read from a request.
/// </summary>
/// <remarks>
/// <para><b>Identities verified</b> is true only when</para>
/// <list type="bullet">
/// <item>the reviewer's account is Active and its email address was verified by a one-time code; and</item>
/// <item>the submitter's account is Active and the submitter's owner profile (national ID, address and proof of address)
/// was approved by an owner-verification reviewer, or, for a workspace submission, the submitter's email is verified.</item>
/// </list>
/// <para><b>Owner conflict</b> is</para>
/// <list type="bullet">
/// <item>true when the reviewer is the submitter or the declared owner's account, or shares any workspace with either;</item>
/// <item>unknown (refused) when the declared owner is an organisation, whose controlling persons are not recorded here;</item>
/// <item>otherwise false. For an owner without an account (a representative's principal) the records can only rule out a
/// link to the representative; the authority workflow therefore also requires every reviewer to confirm, per decision,
/// that they have no conflict with the requester or the named owner, and records that confirmation.</item>
/// </list>
/// <para><b>Workspace affiliation</b> (workspace submissions only) is the reviewer's membership of that workspace.</para>
/// </remarks>
internal sealed class RecordedRelationshipIndependenceProvider(ApplicationDbContext context) : IReviewerIndependenceProvider
{
    public async Task<ReviewerIndependenceFacts> GetAsync(
        Guid reviewerUserId,
        Guid submitterUserId,
        ListingOwnerIdentity declaredOwner,
        Guid? submittingWorkspaceId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(declaredOwner);

        var accounts = await context.Users.AsNoTracking()
            .Where(user => user.Id == reviewerUserId || user.Id == submitterUserId)
            .Select(user => new { user.Id, user.Status, user.EmailVerifiedAt })
            .ToListAsync(cancellationToken);
        var reviewer = accounts.SingleOrDefault(account => account.Id == reviewerUserId);
        var submitter = accounts.SingleOrDefault(account => account.Id == submitterUserId);

        var reviewerVerified = reviewer is { Status: UserStatus.Active, EmailVerifiedAt: not null };
        var submitterVerified = submitter is { Status: UserStatus.Active }
            && (await context.OwnerProfiles.AsNoTracking()
                    .AnyAsync(profile => profile.UserId == submitterUserId && profile.Status == OwnerVerificationStatus.Approved, cancellationToken)
                || (submittingWorkspaceId is not null && submitter.EmailVerifiedAt is not null));

        bool? conflict;

        if (reviewerUserId == submitterUserId || reviewerUserId == declaredOwner.LinkedUserId)
        {
            conflict = true;
        }
        else if (declaredOwner.Kind != ListingOwnerKind.Individual)
        {
            conflict = null;
        }
        else
        {
            var people = declaredOwner.LinkedUserId is { } owner ? new[] { submitterUserId, owner } : new[] { submitterUserId };
            var theirWorkspaces = context.WorkspaceMemberships.AsNoTracking()
                .Where(membership => people.Contains(membership.UserId))
                .Select(membership => membership.WorkspaceId);
            conflict = await context.WorkspaceMemberships.AsNoTracking()
                .AnyAsync(membership => membership.UserId == reviewerUserId && theirWorkspaces.Contains(membership.WorkspaceId), cancellationToken);
        }

        bool? affiliated = submittingWorkspaceId is { } workspace
            ? await context.WorkspaceMemberships.AsNoTracking()
                .AnyAsync(membership => membership.UserId == reviewerUserId && membership.WorkspaceId == workspace, cancellationToken)
            : null;

        return new ReviewerIndependenceFacts(reviewerVerified && submitterVerified, conflict, affiliated);
    }
}
