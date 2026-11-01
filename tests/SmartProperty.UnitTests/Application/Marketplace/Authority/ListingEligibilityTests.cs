using System.Reflection;
using SmartProperty.Application.Marketplace.Authority;
using SmartProperty.Domain.Marketplace;
using Xunit;

namespace SmartProperty.UnitTests.Application.Marketplace.Authority;

/// <summary>
/// The shape of an eligibility result: it can only be built through its factories, and an eligible one cannot exist
/// without verified legal authority.
/// </summary>
public sealed class ListingEligibilityTests
{
    private static readonly DateTimeOffset From = new(2026, 9, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void ThereIsNoPublicConstructor()
    {
        Assert.Empty(typeof(ListingEligibility).GetConstructors(BindingFlags.Public | BindingFlags.Instance));
    }

    [Fact]
    public void EligibleCarriesItsFactsAndNoDenial()
    {
        var result = ListingEligibility.Eligible(Authority(), new SubmissionVerificationFacts(true, true, null));

        Assert.True(result.IsEligible);
        Assert.Equal(ListingEligibilityOutcome.Eligible, result.Outcome);
        Assert.NotNull(result.Authority);
        Assert.NotNull(result.SubmissionFacts);
        Assert.Null(result.Denial);
    }

    [Fact]
    public void AnEligibleResultCannotBeBuiltWithoutVerifiedLegalAuthority()
    {
        Assert.Throws<ArgumentException>(
            () => ListingEligibility.Eligible(Authority(), new SubmissionVerificationFacts(false, true, null)));
    }

    [Fact]
    public void EligibleRequiresItsFacts()
    {
        Assert.Throws<ArgumentNullException>(() => ListingEligibility.Eligible(null!, new SubmissionVerificationFacts(true, null, null)));
        Assert.Throws<ArgumentNullException>(() => ListingEligibility.Eligible(Authority(), null!));
    }

    [Fact]
    public void NotEligibleCarriesOnlyItsReason()
    {
        var result = ListingEligibility.NotEligible(ListingEligibilityDenial.Expired);

        Assert.False(result.IsEligible);
        Assert.Equal(ListingEligibilityOutcome.NotEligible, result.Outcome);
        Assert.Equal(ListingEligibilityDenial.Expired, result.Denial);
        Assert.Null(result.Authority);
        Assert.Null(result.SubmissionFacts);
    }

    [Fact]
    public void AnUndefinedDenialIsRefused()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ListingEligibility.NotEligible((ListingEligibilityDenial)99));
        Assert.Throws<ArgumentOutOfRangeException>(() => ListingEligibility.NotEligible(0));
    }

    [Fact]
    public void UnavailableIsNeitherEligibleNorARefusalOfTheUser()
    {
        var result = ListingEligibility.Unavailable();

        Assert.False(result.IsEligible);
        Assert.Equal(ListingEligibilityOutcome.Unavailable, result.Outcome);
        Assert.Null(result.Authority);
        Assert.Null(result.SubmissionFacts);
        Assert.Null(result.Denial);
    }

    [Fact]
    public void OnlyEligibleIsEligible()
    {
        var results = new[]
        {
            ListingEligibility.NotEligible(ListingEligibilityDenial.NoGrant),
            ListingEligibility.Unavailable(),
        };

        Assert.All(results, result => Assert.False(result.IsEligible));
        Assert.Equal(
            new[] { ListingEligibilityOutcome.Eligible, ListingEligibilityOutcome.NotEligible, ListingEligibilityOutcome.Unavailable },
            Enum.GetValues<ListingEligibilityOutcome>());
    }

    [Fact]
    public void TheOutcomeAndDenialVocabulariesAreExactlyTheApprovedOnes()
    {
        Assert.Equal(
            new[]
            {
                "InvalidRequest", "NoGrant", "MalformedGrant", "NotGrantee", "WrongProperty", "PurposeNotAllowed", "Revoked",
                "NotYetValid", "Expired", "ProvenanceMissing", "ProvenanceInconsistent", "IssuerIsGrantee",
                "ReviewerNotIndependent", "IssuerNotAuthorized", "UnsupportedRepresentation", "OwnerNotLinked", "GrantChanged",
            },
            Enum.GetNames<ListingEligibilityDenial>());
    }

    private static ListingAuthorityFacts Authority()
    {
        return new ListingAuthorityFacts(
            Guid.NewGuid(),
            1,
            Guid.NewGuid(),
            ListingPurpose.Sale,
            Guid.NewGuid(),
            ListingOwnerKind.Individual,
            Guid.NewGuid(),
            null,
            ListingRepresentationKind.IndividualRepresentative,
            From,
            From.AddYears(1));
    }
}
