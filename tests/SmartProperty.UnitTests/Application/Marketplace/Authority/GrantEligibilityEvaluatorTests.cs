using SmartProperty.Application.Marketplace.Authority;
using SmartProperty.Domain.Marketplace;
using Xunit;

namespace SmartProperty.UnitTests.Application.Marketplace.Authority;

/// <summary>
/// The eligibility rules, one failure at a time. Every test starts from a grant that is eligible and breaks exactly
/// one thing, so a refusal can only be explained by that thing.
/// </summary>
public sealed class GrantEligibilityEvaluatorTests
{
    private static readonly Guid Grantee = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid Property = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid Issuer = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private static readonly Guid Reviewer = Guid.Parse("44444444-4444-4444-4444-444444444444");
    private static readonly Guid GrantId = Guid.Parse("55555555-5555-5555-5555-555555555555");
    private static readonly Guid LegalParty = Guid.Parse("66666666-6666-6666-6666-666666666666");
    private static readonly Guid ReviewReference = Guid.Parse("77777777-7777-7777-7777-777777777777");

    private static readonly DateTimeOffset ReviewedAt = new(2026, 8, 15, 9, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset IssuedAt = new(2026, 8, 20, 9, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset ValidFrom = new(2026, 9, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset ValidUntil = new(2027, 9, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Now = new(2026, 10, 20, 12, 0, 0, TimeSpan.Zero);

    // ----- eligible

    [Theory]
    [InlineData(ListingPurpose.Sale)]
    [InlineData(ListingPurpose.Rent)]
    public void AValidOwnerGrantIsEligibleForAnAllowedPurposeAndCarriesOnlyWhatTheGrantSays(ListingPurpose purpose)
    {
        var result = Evaluate(OwnerGrant(), purpose);

        Assert.True(result.IsEligible);
        Assert.Equal(ListingEligibilityOutcome.Eligible, result.Outcome);
        Assert.Null(result.Denial);

        var authority = result.Authority!;
        Assert.Equal(GrantId, authority.GrantId);
        Assert.Equal(7L, authority.GrantVersion);
        Assert.Equal(Property, authority.PropertyId);
        Assert.Equal(purpose, authority.Purpose);
        Assert.Equal(Grantee, authority.ActingUserId);
        Assert.Equal(ListingOwnerKind.Individual, authority.OwnerKind);
        Assert.Equal(LegalParty, authority.LegalPartyReferenceId);
        Assert.Equal(Grantee, authority.LinkedUserId);
        Assert.Equal(ListingRepresentationKind.Owner, authority.Representation);
        Assert.Equal(ValidFrom, authority.ValidFrom);
        Assert.Equal(ValidUntil, authority.ValidUntil);
    }

    [Fact]
    public void AnOwnerGrantEstablishesTheOwnerLinkAndTheLegalAuthorityAndNothingAboutWorkspaces()
    {
        var facts = Evaluate(OwnerGrant(), ListingPurpose.Sale).SubmissionFacts!;

        Assert.True(facts.LegalAuthorityVerified);
        Assert.True(facts.ActingUserIsDeclaredOwnerVerified);
        Assert.Null(facts.WorkspaceOperationAuthorized);
    }

    [Fact]
    public void AnIndividualRepresentativeGrantIsEligibleAndEstablishesNoOwnerFact()
    {
        var result = Evaluate(RepresentativeGrant(), ListingPurpose.Sale);

        Assert.True(result.IsEligible);
        Assert.True(result.SubmissionFacts!.LegalAuthorityVerified);
        Assert.Null(result.SubmissionFacts.ActingUserIsDeclaredOwnerVerified);
        Assert.Null(result.SubmissionFacts.WorkspaceOperationAuthorized);
        Assert.Equal(ListingRepresentationKind.IndividualRepresentative, result.Authority!.Representation);
    }

    [Fact]
    public void ARepresentativeMayActForAnOrganization()
    {
        var grant = RepresentativeGrant() with { OwnerKind = ListingOwnerKind.Organization, LinkedUserId = null };

        Assert.True(Evaluate(grant, ListingPurpose.Sale).IsEligible);
    }

    [Fact]
    public void AGrantForOnePurposeDoesNotCoverTheOther()
    {
        var saleOnly = OwnerGrant() with { AllowsSale = true, AllowsRent = false };
        var rentOnly = OwnerGrant() with { AllowsSale = false, AllowsRent = true };

        Assert.True(Evaluate(saleOnly, ListingPurpose.Sale).IsEligible);
        AssertDenied(Evaluate(saleOnly, ListingPurpose.Rent), ListingEligibilityDenial.PurposeNotAllowed);
        Assert.True(Evaluate(rentOnly, ListingPurpose.Rent).IsEligible);
        AssertDenied(Evaluate(rentOnly, ListingPurpose.Sale), ListingEligibilityDenial.PurposeNotAllowed);
    }

    // ----- the request itself

    [Fact]
    public void AnUnusableRequestIsRefusedWhateverTheGrant()
    {
        AssertDenied(Evaluate(OwnerGrant(), ListingPurpose.Sale, userId: Guid.Empty), ListingEligibilityDenial.InvalidRequest);
        AssertDenied(Evaluate(OwnerGrant(), ListingPurpose.Sale, propertyId: Guid.Empty), ListingEligibilityDenial.InvalidRequest);
        AssertDenied(Evaluate(OwnerGrant(), (ListingPurpose)99), ListingEligibilityDenial.InvalidRequest);
        AssertDenied(Evaluate(OwnerGrant(), ListingPurpose.Sale, at: default(DateTimeOffset)), ListingEligibilityDenial.InvalidRequest);
    }

    [Fact]
    public void NoGrantMeansNotEligible()
    {
        AssertDenied(Evaluate(null, ListingPurpose.Sale), ListingEligibilityDenial.NoGrant);
    }

    [Fact]
    public void AGrantForAnotherUserOrAnotherPropertyIsRefused()
    {
        AssertDenied(Evaluate(OwnerGrant(), ListingPurpose.Sale, userId: Guid.NewGuid()), ListingEligibilityDenial.NotGrantee);
        AssertDenied(Evaluate(OwnerGrant(), ListingPurpose.Sale, propertyId: Guid.NewGuid()), ListingEligibilityDenial.WrongProperty);
    }

    // ----- a grant is not trusted for looking right

    [Fact]
    public void AnActiveGrantWithoutProvenanceIsNotConclusive()
    {
        var anonymous = OwnerGrant() with
        {
            IssuedByUserId = Guid.Empty,
            LegalReviewReferenceId = Guid.Empty,
            LegalReviewedByUserId = Guid.Empty,
        };

        Assert.Equal(AuthorityGrantState.Active, anonymous.State);
        AssertDenied(Evaluate(anonymous, ListingPurpose.Sale), ListingEligibilityDenial.ProvenanceMissing);
    }

    [Fact]
    public void EachProvenanceFieldIsRequired()
    {
        var incomplete = new[]
        {
            OwnerGrant() with { IssuedByUserId = Guid.Empty },
            OwnerGrant() with { LegalReviewReferenceId = Guid.Empty },
            OwnerGrant() with { LegalReviewedByUserId = Guid.Empty },
            OwnerGrant() with { IssuedAt = default },
            OwnerGrant() with { LegalReviewedAt = default },
        };

        foreach (var grant in incomplete)
        {
            AssertDenied(Evaluate(grant, ListingPurpose.Sale), ListingEligibilityDenial.ProvenanceMissing);
        }
    }

    [Fact]
    public void AnIssuerWhoWasNotAuthorizedIsRefused()
    {
        AssertDenied(Evaluate(OwnerGrant() with { IssuerWasAuthorized = false }, ListingPurpose.Sale), ListingEligibilityDenial.IssuerNotAuthorized);
    }

    [Fact]
    public void ANobodyCanIssueTheirOwnGrant()
    {
        AssertDenied(Evaluate(OwnerGrant() with { IssuedByUserId = Grantee }, ListingPurpose.Sale), ListingEligibilityDenial.IssuerIsGrantee);
    }

    [Fact]
    public void TheLegalReviewMustBeIndependentOfTheGranteeAndOfTheIssuer()
    {
        AssertDenied(Evaluate(OwnerGrant() with { LegalReviewedByUserId = Grantee }, ListingPurpose.Sale), ListingEligibilityDenial.ReviewerNotIndependent);
        AssertDenied(Evaluate(OwnerGrant() with { LegalReviewedByUserId = Issuer }, ListingPurpose.Sale), ListingEligibilityDenial.ReviewerNotIndependent);
    }

    [Fact]
    public void TheReviewMustPrecedeTheIssueAndNeitherMayLieInTheFuture()
    {
        AssertDenied(
            Evaluate(OwnerGrant() with { LegalReviewedAt = IssuedAt.AddDays(1) }, ListingPurpose.Sale),
            ListingEligibilityDenial.ProvenanceInconsistent);
        AssertDenied(
            Evaluate(OwnerGrant() with { IssuedAt = Now.AddSeconds(1), LegalReviewedAt = Now.AddDays(-1) }, ListingPurpose.Sale),
            ListingEligibilityDenial.ProvenanceInconsistent);
    }

    // ----- revocation and validity

    [Fact]
    public void ARevokedGrantIsRefused()
    {
        var revoked = OwnerGrant() with { State = AuthorityGrantState.Revoked, RevokedAt = Now.AddDays(-1) };

        AssertDenied(Evaluate(revoked, ListingPurpose.Sale), ListingEligibilityDenial.Revoked);
    }

    [Fact]
    public void ARevokedGrantStaysRevokedEvenIfItsWindowIsOpen()
    {
        var revoked = OwnerGrant() with { State = AuthorityGrantState.Revoked, RevokedAt = Now };

        Assert.True(Now >= revoked.ValidFrom && Now < revoked.ValidUntil);
        AssertDenied(Evaluate(revoked, ListingPurpose.Sale), ListingEligibilityDenial.Revoked);
    }

    [Fact]
    public void AnInconsistentRevocationIsMalformed()
    {
        AssertDenied(Evaluate(OwnerGrant() with { RevokedAt = Now.AddDays(-1) }, ListingPurpose.Sale), ListingEligibilityDenial.MalformedGrant);
        AssertDenied(
            Evaluate(OwnerGrant() with { State = AuthorityGrantState.Revoked, RevokedAt = null }, ListingPurpose.Sale),
            ListingEligibilityDenial.MalformedGrant);
        AssertDenied(
            Evaluate(OwnerGrant() with { State = AuthorityGrantState.Revoked, RevokedAt = default(DateTimeOffset) }, ListingPurpose.Sale),
            ListingEligibilityDenial.MalformedGrant);
    }

    [Fact]
    public void TheValidityWindowStartsInclusivelyAndEndsExclusively()
    {
        Assert.True(Evaluate(OwnerGrant(), ListingPurpose.Sale, at: ValidFrom).IsEligible);
        Assert.True(Evaluate(OwnerGrant(), ListingPurpose.Sale, at: ValidUntil.AddTicks(-1)).IsEligible);
        AssertDenied(Evaluate(OwnerGrant(), ListingPurpose.Sale, at: ValidFrom.AddTicks(-1)), ListingEligibilityDenial.NotYetValid);
        AssertDenied(Evaluate(OwnerGrant(), ListingPurpose.Sale, at: ValidUntil), ListingEligibilityDenial.Expired);
        AssertDenied(Evaluate(OwnerGrant(), ListingPurpose.Sale, at: ValidUntil.AddYears(1)), ListingEligibilityDenial.Expired);
    }

    [Fact]
    public void ADegenerateWindowIsMalformed()
    {
        AssertDenied(Evaluate(OwnerGrant() with { ValidUntil = ValidFrom }, ListingPurpose.Sale), ListingEligibilityDenial.MalformedGrant);
        AssertDenied(Evaluate(OwnerGrant() with { ValidUntil = ValidFrom.AddDays(-1) }, ListingPurpose.Sale), ListingEligibilityDenial.MalformedGrant);
        AssertDenied(Evaluate(OwnerGrant() with { ValidFrom = default }, ListingPurpose.Sale), ListingEligibilityDenial.MalformedGrant);
        AssertDenied(Evaluate(OwnerGrant() with { ValidUntil = default }, ListingPurpose.Sale), ListingEligibilityDenial.MalformedGrant);
    }

    // ----- representation

    [Fact]
    public void WorkspaceRepresentationIsRefusedNotAssumed()
    {
        var grant = RepresentativeGrant() with { Representation = ListingRepresentationKind.WorkspaceRepresentative };

        AssertDenied(Evaluate(grant, ListingPurpose.Sale), ListingEligibilityDenial.UnsupportedRepresentation);
    }

    [Fact]
    public void AnOwnerGrantMustLinkTheGranteeAsAnIndividualOwner()
    {
        AssertDenied(Evaluate(OwnerGrant() with { LinkedUserId = null }, ListingPurpose.Sale), ListingEligibilityDenial.OwnerNotLinked);
        AssertDenied(Evaluate(OwnerGrant() with { LinkedUserId = Guid.NewGuid() }, ListingPurpose.Sale), ListingEligibilityDenial.OwnerNotLinked);
        AssertDenied(
            Evaluate(OwnerGrant() with { OwnerKind = ListingOwnerKind.Organization }, ListingPurpose.Sale),
            ListingEligibilityDenial.OwnerNotLinked);
    }

    // ----- structure

    [Fact]
    public void AMalformedGrantIsRefusedWhateverElseIsRight()
    {
        var malformed = new[]
        {
            OwnerGrant() with { GrantId = Guid.Empty },
            OwnerGrant() with { Version = 0 },
            OwnerGrant() with { Version = -1 },
            OwnerGrant() with { GranteeUserId = Guid.Empty },
            OwnerGrant() with { PropertyId = Guid.Empty },
            OwnerGrant() with { LegalPartyReferenceId = Guid.Empty },
            OwnerGrant() with { LinkedUserId = Guid.Empty },
            OwnerGrant() with { AllowsSale = false, AllowsRent = false },
            OwnerGrant() with { OwnerKind = (ListingOwnerKind)99 },
            OwnerGrant() with { Representation = (ListingRepresentationKind)99 },
            OwnerGrant() with { State = (AuthorityGrantState)99 },
        };

        foreach (var grant in malformed)
        {
            AssertDenied(Evaluate(grant, ListingPurpose.Sale), ListingEligibilityDenial.MalformedGrant);
        }
    }

    [Fact]
    public void EvaluationIsDeterministicAndChangesNothing()
    {
        var grant = OwnerGrant();
        var copy = grant with { };

        var first = Evaluate(grant, ListingPurpose.Rent);
        var second = Evaluate(grant, ListingPurpose.Rent);

        Assert.Equal(copy, grant);
        Assert.Equal(first.Authority, second.Authority);
        Assert.Equal(first.SubmissionFacts, second.SubmissionFacts);
    }

    // ----- revalidation under the lock

    [Fact]
    public void RevalidationAcceptsTheSameUnchangedGrant()
    {
        var pre = Evaluate(OwnerGrant(), ListingPurpose.Sale);

        var locked = GrantEligibilityEvaluator.Revalidate(pre, OwnerGrant(), Grantee, Property, ListingPurpose.Sale, Now);

        Assert.True(locked.IsEligible);
        Assert.Equal(pre.Authority, locked.Authority);
    }

    [Fact]
    public void ARefusedOrUnavailablePreCheckIsFinalAndNeverUpgraded()
    {
        var refused = ListingEligibility.NotEligible(ListingEligibilityDenial.NoGrant);
        var unavailable = ListingEligibility.Unavailable();

        Assert.Same(refused, GrantEligibilityEvaluator.Revalidate(refused, OwnerGrant(), Grantee, Property, ListingPurpose.Sale, Now));
        Assert.Same(unavailable, GrantEligibilityEvaluator.Revalidate(unavailable, OwnerGrant(), Grantee, Property, ListingPurpose.Sale, Now));
    }

    [Fact]
    public void RevalidationRefusesAGrantThatVanishedOrWasRevokedAfterThePreCheck()
    {
        var pre = Evaluate(OwnerGrant(), ListingPurpose.Sale);
        var revoked = OwnerGrant() with { State = AuthorityGrantState.Revoked, RevokedAt = Now.AddMinutes(-1), Version = 8 };

        AssertDenied(
            GrantEligibilityEvaluator.Revalidate(pre, null, Grantee, Property, ListingPurpose.Sale, Now),
            ListingEligibilityDenial.NoGrant);
        AssertDenied(
            GrantEligibilityEvaluator.Revalidate(pre, revoked, Grantee, Property, ListingPurpose.Sale, Now),
            ListingEligibilityDenial.Revoked);
    }

    [Fact]
    public void RevalidationRefusesAnyChangeToTheGrantThePreCheckApproved()
    {
        var pre = Evaluate(OwnerGrant(), ListingPurpose.Sale);

        var changed = new[]
        {
            OwnerGrant() with { Version = 8 },
            OwnerGrant() with { GrantId = Guid.NewGuid() },
            OwnerGrant() with { LegalPartyReferenceId = Guid.NewGuid() },
            OwnerGrant() with { ValidUntil = ValidUntil.AddDays(-30) },
            OwnerGrant() with { ValidFrom = ValidFrom.AddDays(1) },
            OwnerGrant() with { OwnerKind = ListingOwnerKind.Individual, Representation = ListingRepresentationKind.IndividualRepresentative, LinkedUserId = null },
        };

        foreach (var grant in changed)
        {
            AssertDenied(
                GrantEligibilityEvaluator.Revalidate(pre, grant, Grantee, Property, ListingPurpose.Sale, Now),
                ListingEligibilityDenial.GrantChanged);
        }
    }

    [Fact]
    public void RevalidationEvaluatesTheLockedGrantInFullNotJustItsIdentity()
    {
        var pre = Evaluate(OwnerGrant(), ListingPurpose.Sale);
        var sameIdentityButExpired = OwnerGrant();

        AssertDenied(
            GrantEligibilityEvaluator.Revalidate(pre, sameIdentityButExpired, Grantee, Property, ListingPurpose.Sale, ValidUntil),
            ListingEligibilityDenial.Expired);
    }

    [Fact]
    public void RevalidationRequiresAPreCheck()
    {
        Assert.Throws<ArgumentNullException>(
            () => GrantEligibilityEvaluator.Revalidate(null!, OwnerGrant(), Grantee, Property, ListingPurpose.Sale, Now));
    }

    // ----- helpers

    private static ListingEligibility Evaluate(
        AuthorityGrantView? grant,
        ListingPurpose purpose,
        Guid? userId = null,
        Guid? propertyId = null,
        DateTimeOffset? at = null)
    {
        return GrantEligibilityEvaluator.Evaluate(grant, userId ?? Grantee, propertyId ?? Property, purpose, at ?? Now);
    }

    private static void AssertDenied(ListingEligibility result, ListingEligibilityDenial expected)
    {
        Assert.False(result.IsEligible);
        Assert.Equal(ListingEligibilityOutcome.NotEligible, result.Outcome);
        Assert.Equal(expected, result.Denial);
        Assert.Null(result.Authority);
        Assert.Null(result.SubmissionFacts);
    }

    private static AuthorityGrantView OwnerGrant()
    {
        return new AuthorityGrantView(
            GrantId,
            7,
            Grantee,
            Property,
            AllowsSale: true,
            AllowsRent: true,
            ListingOwnerKind.Individual,
            LegalParty,
            LinkedUserId: Grantee,
            ListingRepresentationKind.Owner,
            ValidFrom,
            ValidUntil,
            AuthorityGrantState.Active,
            RevokedAt: null,
            Issuer,
            IssuedAt,
            IssuerWasAuthorized: true,
            ReviewReference,
            Reviewer,
            ReviewedAt);
    }

    private static AuthorityGrantView RepresentativeGrant()
    {
        return OwnerGrant() with
        {
            Representation = ListingRepresentationKind.IndividualRepresentative,
            LinkedUserId = null,
        };
    }
}
