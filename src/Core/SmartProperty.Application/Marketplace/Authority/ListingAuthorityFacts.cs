using SmartProperty.Domain.Marketplace;

namespace SmartProperty.Application.Marketplace.Authority;

/// <summary>
/// The legal-authority facts a draft may be built from, derived solely from a grant that passed evaluation.
/// </summary>
/// <remarks>
/// Nothing here comes from a request. <see cref="ActingUserId"/> is the grantee the grant names, who is the
/// submitter; it is not a statement that this user owns the property. The declared owner is whatever the grant
/// attests, through the legal-party reference.
/// </remarks>
public sealed record ListingAuthorityFacts(
    Guid GrantId,
    long GrantVersion,
    Guid PropertyId,
    ListingPurpose Purpose,
    Guid ActingUserId,
    ListingOwnerKind OwnerKind,
    Guid LegalPartyReferenceId,
    Guid? LinkedUserId,
    ListingRepresentationKind Representation,
    DateTimeOffset ValidFrom,
    DateTimeOffset ValidUntil);
