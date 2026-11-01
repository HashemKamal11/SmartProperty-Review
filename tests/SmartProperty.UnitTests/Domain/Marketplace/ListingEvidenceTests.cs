using SmartProperty.Domain.Marketplace;
using Xunit;

namespace SmartProperty.UnitTests.Domain.Marketplace;

public sealed class ListingEvidenceTests
{
    private static ListingEvidenceReference Evidence() =>
        new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), ListingPurpose.Sale, new string('a', 64));

    [Fact]
    public void CleanAssessment_MatchesByImmutableValuesRatherThanObjectReference()
    {
        var evidence = Evidence();
        var equivalent = new ListingEvidenceReference(evidence.EvidenceId, evidence.VersionId, evidence.PropertyId,
            evidence.Purpose, evidence.Sha256.ToLowerInvariant());
        var assessment = new ListingEvidenceAssessment(Guid.NewGuid(), evidence, EvidenceScanStatus.Clean, MarketplaceFixtures.CreatedAt);

        assessment.EnsureCleanFor(equivalent, MarketplaceFixtures.CreatedAt);
        Assert.Equal(new string('A', 64), evidence.Sha256);
    }

    [Theory]
    [InlineData(EvidenceScanStatus.Unknown)]
    [InlineData(EvidenceScanStatus.Pending)]
    [InlineData(EvidenceScanStatus.Rejected)]
    [InlineData(EvidenceScanStatus.Failed)]
    public void NonCleanAssessments_FailClosed(EvidenceScanStatus status)
    {
        var evidence = Evidence();
        var assessment = new ListingEvidenceAssessment(Guid.NewGuid(), evidence, status, MarketplaceFixtures.CreatedAt);
        Assert.Throws<InvalidOperationException>(() => assessment.EnsureCleanFor(evidence, MarketplaceFixtures.DecidedAt));
        Assert.Equal(status, assessment.Status);
    }

    [Fact]
    public void CleanAssessment_RejectsEveryReferenceMismatchAndFutureAssessment()
    {
        var evidence = Evidence();
        var assessment = new ListingEvidenceAssessment(Guid.NewGuid(), evidence, EvidenceScanStatus.Clean, MarketplaceFixtures.CreatedAt);
        var mismatches = new[]
        {
            new ListingEvidenceReference(Guid.NewGuid(), evidence.VersionId, evidence.PropertyId, evidence.Purpose, evidence.Sha256),
            new ListingEvidenceReference(evidence.EvidenceId, Guid.NewGuid(), evidence.PropertyId, evidence.Purpose, evidence.Sha256),
            new ListingEvidenceReference(evidence.EvidenceId, evidence.VersionId, Guid.NewGuid(), evidence.Purpose, evidence.Sha256),
            new ListingEvidenceReference(evidence.EvidenceId, evidence.VersionId, evidence.PropertyId, ListingPurpose.Rent, evidence.Sha256),
            new ListingEvidenceReference(evidence.EvidenceId, evidence.VersionId, evidence.PropertyId, evidence.Purpose, new string('b', 64))
        };
        foreach (var mismatch in mismatches)
            Assert.Throws<InvalidOperationException>(() => assessment.EnsureCleanFor(mismatch, MarketplaceFixtures.DecidedAt));
        Assert.Throws<InvalidOperationException>(() => assessment.EnsureCleanFor(evidence, MarketplaceFixtures.CreatedAt.AddTicks(-1)));
        Assert.Throws<ArgumentNullException>(() => assessment.EnsureCleanFor(null!, MarketplaceFixtures.DecidedAt));
        Assert.Equal(EvidenceScanStatus.Clean, assessment.Status);
    }

    [Theory]
    [InlineData("")]
    [InlineData("https://example.invalid/evidence")]
    [InlineData("not-a-digest")]
    public void Evidence_RejectsMalformedDigest(string digest)
    {
        Assert.Throws<ArgumentException>(() => new ListingEvidenceReference(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), ListingPurpose.Sale, digest));
    }

    [Fact]
    public void Constructors_RejectMissingIdentitiesNullsAndUndefinedValues()
    {
        var evidence = Evidence();
        Assert.Throws<ArgumentException>(() => new ListingEvidenceReference(Guid.Empty, evidence.VersionId,
            evidence.PropertyId, evidence.Purpose, evidence.Sha256));
        Assert.Throws<ArgumentException>(() => new ListingEvidenceReference(evidence.EvidenceId, Guid.Empty,
            evidence.PropertyId, evidence.Purpose, evidence.Sha256));
        Assert.Throws<ArgumentException>(() => new ListingEvidenceReference(evidence.EvidenceId, evidence.VersionId,
            Guid.Empty, evidence.Purpose, evidence.Sha256));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ListingEvidenceReference(evidence.EvidenceId, evidence.VersionId,
            evidence.PropertyId, (ListingPurpose)99, evidence.Sha256));
        Assert.Throws<ArgumentNullException>(() => new ListingEvidenceReference(evidence.EvidenceId, evidence.VersionId,
            evidence.PropertyId, evidence.Purpose, null!));
        Assert.Throws<ArgumentException>(() => new ListingEvidenceReference(evidence.EvidenceId, evidence.VersionId,
            evidence.PropertyId, evidence.Purpose, new string('z', 64)));
        Assert.Throws<ArgumentException>(() => new ListingEvidenceAssessment(Guid.Empty, evidence,
            EvidenceScanStatus.Clean, MarketplaceFixtures.CreatedAt));
        Assert.Throws<ArgumentNullException>(() => new ListingEvidenceAssessment(Guid.NewGuid(), null!,
            EvidenceScanStatus.Clean, MarketplaceFixtures.CreatedAt));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ListingEvidenceAssessment(Guid.NewGuid(), evidence,
            (EvidenceScanStatus)99, MarketplaceFixtures.CreatedAt));
    }

    [Theory]
    [InlineData(typeof(ListingEvidenceReference))]
    [InlineData(typeof(ListingEvidenceAssessment))]
    [InlineData(typeof(ListingLegalAuthority))]
    [InlineData(typeof(ListingReviewContext))]
    public void ValueObjects_ExposeNoSetters(Type type)
    {
        Assert.All(type.GetProperties(), property => Assert.Null(property.SetMethod));
    }
}
