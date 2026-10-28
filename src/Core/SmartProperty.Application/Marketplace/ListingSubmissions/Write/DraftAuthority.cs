using SmartProperty.Application.Marketplace.Authority;
using SmartProperty.Domain.Marketplace;

namespace SmartProperty.Application.Marketplace.ListingSubmissions.Write;

/// <summary>
/// The legal authority a draft carries, built from a verified grant and compared with one later.
/// </summary>
/// <remarks>
/// A draft carries a snapshot of the grant that allowed it, and it is never editable. It is built only from
/// <see cref="ListingAuthorityFacts"/>, which the grant evaluator derived under the locks, so a request cannot
/// supply any part of it. At submission it must still describe the same party and actor as the grant now in force,
/// and its own window must still cover the moment of submission.
/// </remarks>
internal static class DraftAuthority
{
    public static ListingLegalAuthority FromFacts(ListingAuthorityFacts facts)
    {
        ArgumentNullException.ThrowIfNull(facts);

        return new ListingLegalAuthority(
            facts.PropertyId,
            facts.Purpose,
            new ListingOwnerIdentity(facts.OwnerKind, facts.LegalPartyReferenceId, facts.LinkedUserId),
            facts.ActingUserId,
            facts.Representation,
            workspaceId: null,
            facts.ValidFrom,
            facts.ValidUntil);
    }

    public static bool IsStillValid(ListingLegalAuthority snapshot, ListingAuthorityFacts current, DateTimeOffset at)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(current);

        return snapshot.PropertyId == current.PropertyId
            && snapshot.Purpose == current.Purpose
            && snapshot.ActingUserId == current.ActingUserId
            && snapshot.Representation == current.Representation
            && snapshot.WorkspaceId is null
            && snapshot.DeclaredOwner.Kind == current.OwnerKind
            && snapshot.DeclaredOwner.LegalPartyReferenceId == current.LegalPartyReferenceId
            && snapshot.DeclaredOwner.LinkedUserId == current.LinkedUserId
            && at >= snapshot.ValidFrom
            && (snapshot.ValidUntil is null || at < snapshot.ValidUntil);
    }
}
