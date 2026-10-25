namespace SmartProperty.Domain.Marketplace;

/// <summary>
/// Immutable declaration for one property and purpose, not proof of ownership or legal authority.
/// Callers must obtain current legal verification and separate operational Workspace authorization
/// from trusted services. This type does not verify documents, membership, expiry policy or revocation.
/// </summary>
public sealed class ListingLegalAuthority
{
    public ListingLegalAuthority(
        Guid propertyId,
        ListingPurpose purpose,
        ListingOwnerIdentity declaredOwner,
        Guid actingUserId,
        ListingRepresentationKind representation,
        Guid? workspaceId,
        DateTimeOffset validFrom,
        DateTimeOffset? validUntil)
    {
        ArgumentNullException.ThrowIfNull(declaredOwner);
        if (propertyId == Guid.Empty || actingUserId == Guid.Empty)
            throw new ArgumentException("Property, owner and actor identities must be nonempty.");
        if (!Enum.IsDefined(purpose))
            throw new ArgumentOutOfRangeException(nameof(purpose));
        if (!Enum.IsDefined(representation))
            throw new ArgumentOutOfRangeException(nameof(representation));
        if (representation == ListingRepresentationKind.WorkspaceRepresentative)
        {
            if (workspaceId is null || workspaceId == Guid.Empty)
                throw new ArgumentException("Workspace representation requires a Workspace identity.", nameof(workspaceId));
        }
        else if (workspaceId is not null)
            throw new ArgumentException("Only Workspace representation may specify a Workspace.", nameof(workspaceId));
        if (representation == ListingRepresentationKind.Owner)
        {
            if (declaredOwner.Kind != ListingOwnerKind.Individual)
                throw new ArgumentException("Organizations must act through a representative.", nameof(representation));
            if (declaredOwner.LinkedUserId.HasValue && actingUserId != declaredOwner.LinkedUserId.Value)
                throw new ArgumentException("An owner declaration must identify the linked owner as the actor.", nameof(actingUserId));
        }
        if (validUntil.HasValue && validUntil.Value <= validFrom)
            throw new ArgumentException("The validity end must follow its start.", nameof(validUntil));

        PropertyId = propertyId;
        Purpose = purpose;
        DeclaredOwner = declaredOwner;
        ActingUserId = actingUserId;
        Representation = representation;
        WorkspaceId = workspaceId;
        ValidFrom = validFrom;
        ValidUntil = validUntil;
    }

    public Guid PropertyId { get; }
    public ListingPurpose Purpose { get; }
    public ListingOwnerIdentity DeclaredOwner { get; }
    public Guid ActingUserId { get; }
    public ListingRepresentationKind Representation { get; }
    public Guid? WorkspaceId { get; }
    public DateTimeOffset ValidFrom { get; }
    public DateTimeOffset? ValidUntil { get; }

    /// <summary>
    /// Checks caller-supplied trusted verification results, never client assertions.
    /// Validity is start-inclusive and end-exclusive; no end means no declared end, not perpetual verification.
    /// Workspace operational permission never substitutes for verified legal authority.
    /// Unlinked individuals acting as owner require an explicit trusted actor-to-owner match.
    /// </summary>
    public void EnsureApplicable(
        Guid propertyId, ListingPurpose purpose, Guid actingUserId, Guid? workspaceId,
        DateTimeOffset at, bool? legalAuthorityVerified, bool? workspaceOperationAuthorized,
        bool? actingUserIsDeclaredOwnerVerified = null)
    {
        if (PropertyId != propertyId || Purpose != purpose || ActingUserId != actingUserId || WorkspaceId != workspaceId)
            throw new InvalidOperationException("The declaration does not cover this property, purpose or representation context.");
        if (at < ValidFrom || (ValidUntil.HasValue && at >= ValidUntil.Value))
            throw new InvalidOperationException("The declaration is outside its validity interval.");
        if (legalAuthorityVerified != true)
            throw new InvalidOperationException("Current legal authority must be independently verified.");
        if (Representation == ListingRepresentationKind.Owner)
        {
            if (actingUserIsDeclaredOwnerVerified == false
                || (!DeclaredOwner.LinkedUserId.HasValue && actingUserIsDeclaredOwnerVerified != true))
                throw new InvalidOperationException("The actor must be verified as the declared individual owner.");
        }
        if (Representation == ListingRepresentationKind.WorkspaceRepresentative && workspaceOperationAuthorized != true)
            throw new InvalidOperationException("Workspace operational authorization is separately required.");
    }
}
