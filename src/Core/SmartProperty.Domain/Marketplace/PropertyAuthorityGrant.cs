namespace SmartProperty.Domain.Marketplace;

/// <summary>
/// One property's purpose-specific grant. Issue accepts only trusted Application verification facts,
/// never request flags. No identity, permission or legal review is established by constructing this type.
/// The write repository must reread and lock the persisted grant before use; revocation requires the same lock.
/// </summary>
public sealed class PropertyAuthorityGrant
{
    private PropertyAuthorityGrant(Guid id, ListingLegalAuthority authority, ListingReviewContext issuer,
        Guid legalReviewReferenceId, ListingReviewContext legalReviewer, DateTimeOffset reviewedAt, DateTimeOffset issuedAt)
    {
        Id = id;
        Authority = authority;
        Issuer = issuer;
        LegalReviewReferenceId = legalReviewReferenceId;
        LegalReviewer = legalReviewer;
        LegalReviewedAt = reviewedAt;
        IssuedAt = issuedAt;
    }

    public Guid Id { get; }
    public ListingLegalAuthority Authority { get; }
    public Guid PropertyId => Authority.PropertyId;
    public ListingPurpose Purpose => Authority.Purpose;
    public Guid GranteeUserId => Authority.ActingUserId;
    public ListingReviewContext Issuer { get; }
    public Guid LegalReviewReferenceId { get; }
    public ListingReviewContext LegalReviewer { get; }
    public DateTimeOffset LegalReviewedAt { get; }
    public DateTimeOffset IssuedAt { get; }
    public long Version { get; private set; } = 1;
    public DateTimeOffset? RevokedAt { get; private set; }
    public Guid? RevokedByUserId { get; private set; }

    public static PropertyAuthorityGrant Issue(Guid id, ListingLegalAuthority authority,
        ListingReviewContext issuer, Guid legalReviewReferenceId, ListingReviewContext legalReviewer,
        DateTimeOffset legalReviewedAt, DateTimeOffset issuedAt, bool? legalAuthorityVerified)
    {
        ArgumentNullException.ThrowIfNull(authority);
        if (id == Guid.Empty || legalReviewReferenceId == Guid.Empty)
            throw new ArgumentException("Grant and legal review identities are required.");
        if (legalAuthorityVerified != true)
            throw new InvalidOperationException("Independent legal verification is required.");
        if (authority.WorkspaceId is not null
            || authority.Representation is not (ListingRepresentationKind.Owner or ListingRepresentationKind.IndividualRepresentative))
            throw new InvalidOperationException("This grant does not support Workspace representation.");
        if (authority.Representation == ListingRepresentationKind.Owner
            && authority.DeclaredOwner.LinkedUserId != authority.ActingUserId)
            throw new InvalidOperationException("Owner grants require a verified linked owner.");
        if (authority.ValidFrom == default || authority.ValidUntil is null
            || legalReviewedAt == default || issuedAt == default || legalReviewedAt > issuedAt
            || issuedAt >= authority.ValidUntil)
            throw new InvalidOperationException("Grant validity and review chronology must be established.");
        EnsureReviewer(authority, issuer);
        EnsureReviewer(authority, legalReviewer);
        if (issuer.ReviewerUserId == legalReviewer.ReviewerUserId)
            throw new InvalidOperationException("Legal reviewer must be independent of the issuer.");
        return new PropertyAuthorityGrant(id, authority, issuer, legalReviewReferenceId, legalReviewer, legalReviewedAt, issuedAt);
    }

    public void EnsureApplicable(Guid granteeUserId, Guid propertyId, ListingPurpose purpose, DateTimeOffset at, long expectedVersion)
    {
        if (expectedVersion < 1 || expectedVersion != Version || RevokedAt is not null
            || granteeUserId != GranteeUserId || propertyId != PropertyId || purpose != Purpose
            || at < IssuedAt || at < Authority.ValidFrom || at >= Authority.ValidUntil)
            throw new InvalidOperationException("No current matching authority grant.");
    }

    public void Revoke(long expectedVersion, ListingReviewContext reviewer, DateTimeOffset at)
    {
        if (expectedVersion < 1 || expectedVersion != Version || RevokedAt is not null || at < IssuedAt)
            throw new InvalidOperationException("Grant cannot be revoked in this state or revision.");
        EnsureReviewer(Authority, reviewer);
        var nextVersion = checked(Version + 1);
        RevokedByUserId = reviewer.ReviewerUserId;
        RevokedAt = at;
        Version = nextVersion;
    }

    private static void EnsureReviewer(ListingLegalAuthority authority, ListingReviewContext reviewer)
    {
        ArgumentNullException.ThrowIfNull(reviewer);
        var owner = authority.DeclaredOwner;
        if (reviewer.SubmitterUserId != authority.ActingUserId || reviewer.SubmittingWorkspaceId != authority.WorkspaceId
            || reviewer.DeclaredOwner.Kind != owner.Kind || reviewer.DeclaredOwner.LegalPartyReferenceId != owner.LegalPartyReferenceId
            || reviewer.DeclaredOwner.LinkedUserId != owner.LinkedUserId)
            throw new InvalidOperationException("Review facts must describe this grant's grantee and owner.");
        reviewer.EnsureIndependentReviewer();
    }
}
