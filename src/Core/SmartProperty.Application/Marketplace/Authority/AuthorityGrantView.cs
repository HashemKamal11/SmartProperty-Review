using SmartProperty.Domain.Marketplace;

namespace SmartProperty.Application.Marketplace.Authority;

/// <summary>
/// What the trusted persistence layer reports about one property authority grant: who may list which property for
/// which purposes, on whose behalf, for how long, and how the grant came to exist.
/// </summary>
/// <remarks>
/// <para>
/// This is a read model, not proof. It is trustworthy only because of where it comes from: it must be produced by the
/// persistence implementation of <c>IListingSubmissionWriteRepository</c>, from a persisted grant that the Domain
/// validated when an authorized Platform Admin issued it, read inside the same transaction and under the same
/// property lock as the write it is about to allow. Application code never builds one from a request, a token claim
/// or any client-supplied value, and never from the mere existence of a row.
/// </para>
/// <para>
/// Because a record's shape says nothing about how it was made, <see cref="GrantEligibilityEvaluator"/> does not
/// trust it either. It re-checks the grantee, the property, the purpose, the validity window, the revocation state
/// and the provenance fields below (an independent issuer and an independent legal review that both precede use) and
/// refuses a grant on any inconsistency. An Active row alone is never conclusive.
/// </para>
/// <para>
/// <see cref="Version"/> is the grant's concurrency version. A caller that evaluated eligibility before taking the
/// lock must evaluate again on the locked read and compare, so a grant that changed in between is not honoured.
/// </para>
/// The Workspace is deliberately absent: Workspace representation is not supported by this model yet.
/// </remarks>
public sealed record AuthorityGrantView(
    Guid GrantId,
    long Version,
    Guid GranteeUserId,
    Guid PropertyId,
    bool AllowsSale,
    bool AllowsRent,
    ListingOwnerKind OwnerKind,
    Guid LegalPartyReferenceId,
    Guid? LinkedUserId,
    ListingRepresentationKind Representation,
    DateTimeOffset ValidFrom,
    DateTimeOffset ValidUntil,
    AuthorityGrantState State,
    DateTimeOffset? RevokedAt,
    Guid IssuedByUserId,
    DateTimeOffset IssuedAt,
    bool IssuerWasAuthorized,
    Guid LegalReviewReferenceId,
    Guid LegalReviewedByUserId,
    DateTimeOffset LegalReviewedAt);
