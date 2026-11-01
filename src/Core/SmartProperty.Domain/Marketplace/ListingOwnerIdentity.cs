namespace SmartProperty.Domain.Marketplace;

/// <summary>
/// Immutable declared legal-party identity, not proof of ownership or an identity-verification result.
/// LegalPartyReferenceId is a protected opaque reference: callers must not expose it in public contracts
/// or logs. Access control and identity resolution belong to trusted Application services.
/// </summary>
public sealed class ListingOwnerIdentity
{
    public ListingOwnerIdentity(ListingOwnerKind kind, Guid legalPartyReferenceId, Guid? linkedUserId = null)
    {
        if (!Enum.IsDefined(kind))
            throw new ArgumentOutOfRangeException(nameof(kind));
        if (legalPartyReferenceId == Guid.Empty)
            throw new ArgumentException("A legal-party reference is required.", nameof(legalPartyReferenceId));
        if (linkedUserId == Guid.Empty)
            throw new ArgumentException("A supplied user identity must be nonempty.", nameof(linkedUserId));
        if (kind == ListingOwnerKind.Organization && linkedUserId.HasValue)
            throw new ArgumentException("Only individual owners may have a linked user.", nameof(linkedUserId));

        Kind = kind;
        LegalPartyReferenceId = legalPartyReferenceId;
        LinkedUserId = linkedUserId;
    }

    public ListingOwnerKind Kind { get; }
    public Guid LegalPartyReferenceId { get; }
    public Guid? LinkedUserId { get; }
}
