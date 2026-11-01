using SmartProperty.Domain.Marketplace;

namespace SmartProperty.Application.Marketplace.Authority;

/// <summary>
/// Decides, from one property authority grant, whether a user may list a property for a purpose at a given time.
/// </summary>
/// <remarks>
/// <para>
/// A pure function: no persistence, no clock and no request input. The caller supplies the grant it read from the
/// trusted repository, the authenticated user, the property, the purpose and the time. The evaluator fails closed.
/// It returns <see cref="ListingEligibilityOutcome.Eligible"/> only when every condition below holds, and any other
/// state of the data, including a shape it does not recognize, is a refusal.
/// </para>
/// <list type="bullet">
/// <item>The grant names this user as grantee and this property, and allows this purpose.</item>
/// <item>The grant is Active and has no revocation time.</item>
/// <item>Now falls inside the validity window: at or after its start, strictly before its end.</item>
/// <item>The provenance is complete and consistent. The issuer and the legal reviewer are identified, the issuer was
/// authorized when issuing, the issuer is not the grantee, the legal reviewer is neither the grantee nor the issuer,
/// and the review precedes the issue and both precede now.</item>
/// <item>The representation is one this model supports: Owner or IndividualRepresentative. Workspace representation
/// is refused, not assumed. An Owner grant links the grantee as the owner's user.</item>
/// </list>
/// <para>
/// The legal reviewer being different from the issuer is the conservative reading of "an independent legal review". It
/// can only make the evaluator stricter than a later grant-administration decision, never more permissive.
/// </para>
/// <para>
/// Being eligible is not the same as being verified for submission. It says the grant is in force and well formed. The
/// submission facts it derives cover legal authority and the owner link only; the evidence and its scan results come
/// from the evidence provider, and nothing here can produce them.
/// </para>
/// </remarks>
public static class GrantEligibilityEvaluator
{
    public static ListingEligibility Evaluate(
        AuthorityGrantView? grant,
        Guid actingUserId,
        Guid propertyId,
        ListingPurpose purpose,
        DateTimeOffset at)
    {
        if (actingUserId == Guid.Empty || propertyId == Guid.Empty || !Enum.IsDefined(purpose) || at == default)
        {
            return Deny(ListingEligibilityDenial.InvalidRequest);
        }

        if (grant is null)
        {
            return Deny(ListingEligibilityDenial.NoGrant);
        }

        if (IsMalformed(grant))
        {
            return Deny(ListingEligibilityDenial.MalformedGrant);
        }

        if (grant.GranteeUserId != actingUserId)
        {
            return Deny(ListingEligibilityDenial.NotGrantee);
        }

        if (grant.PropertyId != propertyId)
        {
            return Deny(ListingEligibilityDenial.WrongProperty);
        }

        if (!Allows(grant, purpose))
        {
            return Deny(ListingEligibilityDenial.PurposeNotAllowed);
        }

        if (grant.State == AuthorityGrantState.Revoked)
        {
            return Deny(ListingEligibilityDenial.Revoked);
        }

        if (ProvenanceDenial(grant, at) is { } provenance)
        {
            return Deny(provenance);
        }

        if (at < grant.ValidFrom)
        {
            return Deny(ListingEligibilityDenial.NotYetValid);
        }

        if (at >= grant.ValidUntil)
        {
            return Deny(ListingEligibilityDenial.Expired);
        }

        if (grant.Representation is not (ListingRepresentationKind.Owner or ListingRepresentationKind.IndividualRepresentative))
        {
            return Deny(ListingEligibilityDenial.UnsupportedRepresentation);
        }

        if (grant.Representation == ListingRepresentationKind.Owner
            && (grant.OwnerKind != ListingOwnerKind.Individual || grant.LinkedUserId != grant.GranteeUserId))
        {
            return Deny(ListingEligibilityDenial.OwnerNotLinked);
        }

        var authority = new ListingAuthorityFacts(
            grant.GrantId,
            grant.Version,
            grant.PropertyId,
            purpose,
            actingUserId,
            grant.OwnerKind,
            grant.LegalPartyReferenceId,
            grant.LinkedUserId,
            grant.Representation,
            grant.ValidFrom,
            grant.ValidUntil);

        // The grant attests the owner link for an Owner representation, and the Domain only needs the fact for
        // that representation. A representative has no owner fact to establish. Workspace operation authority is not
        // supported, so it is never claimed.
        var submissionFacts = new SubmissionVerificationFacts(
            LegalAuthorityVerified: true,
            ActingUserIsDeclaredOwnerVerified: grant.Representation == ListingRepresentationKind.Owner ? true : null,
            WorkspaceOperationAuthorized: null);

        return ListingEligibility.Eligible(authority, submissionFacts);
    }

    /// <summary>
    /// Evaluates the grant read under the property lock and requires it to be the very grant the earlier,
    /// lock-free evaluation approved, unchanged. The earlier result is never enough by itself.
    /// </summary>
    public static ListingEligibility Revalidate(
        ListingEligibility preCheck,
        AuthorityGrantView? lockedGrant,
        Guid actingUserId,
        Guid propertyId,
        ListingPurpose purpose,
        DateTimeOffset at)
    {
        ArgumentNullException.ThrowIfNull(preCheck);

        // A pre-check that was not an approval is final: it is never upgraded by a later read.
        if (!preCheck.IsEligible)
        {
            return preCheck;
        }

        var locked = Evaluate(lockedGrant, actingUserId, propertyId, purpose, at);

        if (!locked.IsEligible)
        {
            return locked;
        }

        // Same grant, same version, same derived authority. Anything else means it changed between the two reads.
        if (locked.Authority!.GrantId != preCheck.Authority!.GrantId
            || locked.Authority.GrantVersion != preCheck.Authority.GrantVersion
            || !PreservesAuthority(preCheck.Authority, locked.Authority))
        {
            return Deny(ListingEligibilityDenial.GrantChanged);
        }

        return locked;
    }

    private static bool PreservesAuthority(ListingAuthorityFacts before, ListingAuthorityFacts after)
    {
        return before.PropertyId == after.PropertyId
            && before.Purpose == after.Purpose
            && before.ActingUserId == after.ActingUserId
            && before.OwnerKind == after.OwnerKind
            && before.LegalPartyReferenceId == after.LegalPartyReferenceId
            && before.LinkedUserId == after.LinkedUserId
            && before.Representation == after.Representation
            && before.ValidFrom == after.ValidFrom
            && before.ValidUntil == after.ValidUntil;
    }

    // Structural soundness only: ids present, enumerations defined, times present, revocation consistent with state.
    private static bool IsMalformed(AuthorityGrantView grant)
    {
        if (grant.GrantId == Guid.Empty
            || grant.Version < 1
            || grant.GranteeUserId == Guid.Empty
            || grant.PropertyId == Guid.Empty
            || grant.LegalPartyReferenceId == Guid.Empty
            || grant.LinkedUserId == Guid.Empty
            || (!grant.AllowsSale && !grant.AllowsRent)
            || !Enum.IsDefined(grant.OwnerKind)
            || !Enum.IsDefined(grant.Representation)
            || !Enum.IsDefined(grant.State)
            || grant.ValidFrom == default
            || grant.ValidUntil == default
            || grant.ValidUntil <= grant.ValidFrom)
        {
            return true;
        }

        return grant.State switch
        {
            AuthorityGrantState.Active => grant.RevokedAt is not null,
            AuthorityGrantState.Revoked => grant.RevokedAt is not { } revokedAt || revokedAt == default,
            _ => true
        };
    }

    private static bool Allows(AuthorityGrantView grant, ListingPurpose purpose)
    {
        return purpose switch
        {
            ListingPurpose.Sale => grant.AllowsSale,
            ListingPurpose.Rent => grant.AllowsRent,
            _ => false
        };
    }

    private static ListingEligibilityDenial? ProvenanceDenial(AuthorityGrantView grant, DateTimeOffset at)
    {
        if (grant.IssuedByUserId == Guid.Empty
            || grant.LegalReviewReferenceId == Guid.Empty
            || grant.LegalReviewedByUserId == Guid.Empty
            || grant.IssuedAt == default
            || grant.LegalReviewedAt == default)
        {
            return ListingEligibilityDenial.ProvenanceMissing;
        }

        if (!grant.IssuerWasAuthorized)
        {
            return ListingEligibilityDenial.IssuerNotAuthorized;
        }

        if (grant.IssuedByUserId == grant.GranteeUserId)
        {
            return ListingEligibilityDenial.IssuerIsGrantee;
        }

        if (grant.LegalReviewedByUserId == grant.GranteeUserId || grant.LegalReviewedByUserId == grant.IssuedByUserId)
        {
            return ListingEligibilityDenial.ReviewerNotIndependent;
        }

        // The review comes first, then the issue, and neither lies in the future.
        if (grant.LegalReviewedAt > grant.IssuedAt || grant.IssuedAt > at)
        {
            return ListingEligibilityDenial.ProvenanceInconsistent;
        }

        return null;
    }

    private static ListingEligibility Deny(ListingEligibilityDenial denial)
    {
        return ListingEligibility.NotEligible(denial);
    }
}
