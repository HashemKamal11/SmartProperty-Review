namespace SmartProperty.Domain.Marketplace;

/// <summary>
/// Immutable trusted Application-layer facts for reviewer eligibility, not an authentication mechanism.
/// No Platform permission or Workspace membership is inferred here. All nullable verification facts must
/// come from authoritative checks, never a client payload. Owner references include individuals without accounts and organizations.
/// </summary>
public sealed class ListingReviewContext
{
    public ListingReviewContext(
        Guid reviewerUserId, Guid submitterUserId, ListingOwnerIdentity declaredOwner, Guid? submittingWorkspaceId,
        bool? identitiesVerified, bool? platformReviewAuthorized, bool? reviewerAffiliatedWithWorkspace,
        bool? reviewerHasOwnerConflict)
    {
        ArgumentNullException.ThrowIfNull(declaredOwner);
        if (reviewerUserId == Guid.Empty || submitterUserId == Guid.Empty)
            throw new ArgumentException("Reviewer, submitter and owner identities must be nonempty.");
        if (submittingWorkspaceId == Guid.Empty)
            throw new ArgumentException("A supplied Workspace identity must be nonempty.", nameof(submittingWorkspaceId));
        if (submittingWorkspaceId is null && reviewerAffiliatedWithWorkspace is not null)
            throw new ArgumentException("Affiliation facts require a submitting Workspace.", nameof(reviewerAffiliatedWithWorkspace));

        ReviewerUserId = reviewerUserId;
        SubmitterUserId = submitterUserId;
        DeclaredOwner = declaredOwner;
        SubmittingWorkspaceId = submittingWorkspaceId;
        IdentitiesVerified = identitiesVerified;
        PlatformReviewAuthorized = platformReviewAuthorized;
        ReviewerAffiliatedWithWorkspace = reviewerAffiliatedWithWorkspace;
        ReviewerHasOwnerConflict = reviewerHasOwnerConflict;
    }

    public Guid ReviewerUserId { get; }
    public Guid SubmitterUserId { get; }
    public ListingOwnerIdentity DeclaredOwner { get; }
    public Guid? SubmittingWorkspaceId { get; }
    public bool? IdentitiesVerified { get; }
    public bool? PlatformReviewAuthorized { get; }
    public bool? ReviewerAffiliatedWithWorkspace { get; }

    /// <summary>
    /// Trusted conflict determination: true includes being the individual owner or a representative or
    /// controlling person of the owning organization. Null means unknown, never independent.
    /// </summary>
    public bool? ReviewerHasOwnerConflict { get; }

    public void EnsureIndependentReviewer()
    {
        if (IdentitiesVerified != true || PlatformReviewAuthorized != true)
            throw new InvalidOperationException("Verified identities and Platform review authorization are required.");
        if (ReviewerUserId == SubmitterUserId || ReviewerUserId == DeclaredOwner.LinkedUserId)
            throw new InvalidOperationException("The reviewer cannot be the submitter or declared owner.");
        if (ReviewerHasOwnerConflict != false)
            throw new InvalidOperationException("Verified absence of owner conflict is required.");
        if (SubmittingWorkspaceId.HasValue && ReviewerAffiliatedWithWorkspace != false)
            throw new InvalidOperationException("Verified non-affiliation with the submitting Workspace is required.");
    }
}
