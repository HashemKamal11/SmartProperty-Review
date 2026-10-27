using System.Reflection;
using SmartProperty.Application.Abstractions.Marketplace;
using SmartProperty.Application.Marketplace.ListingSubmissions.Write;
using SmartProperty.Common.Results;
using SmartProperty.Domain.Marketplace;
using SmartProperty.Domain.PropertyRegistry;
using Xunit;

namespace SmartProperty.UnitTests.Application.Marketplace.ListingSubmissions.Write;

/// <summary>
/// What must be true of a draft's evidence before a submission is attempted: it exists, every piece has exactly one
/// matching Clean assessment from the trusted provider, and nothing short of that passes.
/// </summary>
public sealed class SubmissionEvidenceVerifierTests
{
    private static readonly Guid PropertyId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid UserId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly DateTimeOffset Created = new(2026, 10, 1, 8, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Now = new(2026, 10, 20, 12, 0, 0, TimeSpan.Zero);
    private const string Digest = "A3F1C9D2B4E5F60718293A4B5C6D7E8F9A0B1C2D3E4F5061728394A5B6C7D8E9";

    private readonly FakeEvidenceAssessmentProvider _provider = new();

    [Fact]
    public async Task EveryPieceWithOneMatchingCleanAssessmentVerifiesAndReturnsThoseAssessments()
    {
        var draft = DraftWithEvidence(3);
        _provider.Respond(draft.Evidence.Select(reference => Assessment(reference)).ToArray());

        var result = await SubmissionEvidenceVerifier.VerifyAsync(draft, _provider, Now);

        Assert.True(result.IsSuccess);
        Assert.Equal(3, result.Value.Count);
        Assert.Equal(
            draft.Evidence.Select(reference => reference.EvidenceId).Order(),
            result.Value.Select(assessment => assessment.Evidence.EvidenceId).Order());
    }

    [Fact]
    public async Task TheProviderIsAskedAboutExactlyTheDraftsReferencesAtTheEvaluationTime()
    {
        using var source = new CancellationTokenSource();
        var draft = DraftWithEvidence(2);
        _provider.Respond(draft.Evidence.Select(reference => Assessment(reference)).ToArray());

        await SubmissionEvidenceVerifier.VerifyAsync(draft, _provider, Now, source.Token);

        Assert.Equal(draft.Evidence.Select(reference => reference.EvidenceId).Order(), _provider.AskedAbout!.Select(reference => reference.EvidenceId).Order());
        Assert.Equal(Now, _provider.AskedAt);
        Assert.Equal(source.Token, _provider.CancellationToken);
    }

    [Fact]
    public async Task ADraftWithNoEvidenceIsRefusedWithoutAskingTheProvider()
    {
        // The Domain cannot build a draft without evidence yet; when it can, the explicit check here is what stops a
        // submission, because the Domain compares assessment and reference counts and zero equals zero.
        var draft = DraftWithoutEvidence();
        _provider.Respond([]);

        var result = await SubmissionEvidenceVerifier.VerifyAsync(draft, _provider, Now);

        AssertUnverified(result);
        Assert.Equal(0, _provider.CallCount);
    }

    [Fact]
    public async Task AnUnavailableProviderIsNeverTreatedAsClean()
    {
        var draft = DraftWithEvidence(1);
        _provider.Unavailable();

        AssertUnverified(await SubmissionEvidenceVerifier.VerifyAsync(draft, _provider, Now));
    }

    [Fact]
    public async Task AMissingAssessmentIsRefused()
    {
        var draft = DraftWithEvidence(3);
        _provider.Respond(draft.Evidence.Take(2).Select(reference => Assessment(reference)).ToArray());

        AssertUnverified(await SubmissionEvidenceVerifier.VerifyAsync(draft, _provider, Now));
    }

    [Fact]
    public async Task AnExtraAssessmentIsRefused()
    {
        var draft = DraftWithEvidence(1);
        var stranger = Reference();
        _provider.Respond([Assessment(draft.Evidence[0]), Assessment(stranger)]);

        AssertUnverified(await SubmissionEvidenceVerifier.VerifyAsync(draft, _provider, Now));
    }

    [Fact]
    public async Task TwoAssessmentsForOnePieceAndNoneForAnotherIsRefusedEvenThoughTheCountsAgree()
    {
        var draft = DraftWithEvidence(2);
        _provider.Respond([Assessment(draft.Evidence[0]), Assessment(draft.Evidence[0])]);

        AssertUnverified(await SubmissionEvidenceVerifier.VerifyAsync(draft, _provider, Now));
    }

    [Theory]
    [InlineData(EvidenceScanStatus.Unknown)]
    [InlineData(EvidenceScanStatus.Pending)]
    [InlineData(EvidenceScanStatus.Rejected)]
    [InlineData(EvidenceScanStatus.Failed)]
    public async Task AnythingOtherThanCleanIsRefused(EvidenceScanStatus status)
    {
        var draft = DraftWithEvidence(2);
        _provider.Respond([Assessment(draft.Evidence[0]), Assessment(draft.Evidence[1], status)]);

        AssertUnverified(await SubmissionEvidenceVerifier.VerifyAsync(draft, _provider, Now));
    }

    [Fact]
    public async Task AnAssessmentFromTheFutureIsRefused()
    {
        var draft = DraftWithEvidence(1);
        _provider.Respond([Assessment(draft.Evidence[0], assessedAt: Now.AddSeconds(1))]);

        AssertUnverified(await SubmissionEvidenceVerifier.VerifyAsync(draft, _provider, Now));
    }

    [Fact]
    public async Task AnAssessmentOfDifferentBytesOrAnotherVersionForTheSameEvidenceIsRefused()
    {
        var draft = DraftWithEvidence(1);
        var original = draft.Evidence[0];
        var otherDigest = new ListingEvidenceReference(original.EvidenceId, original.VersionId, original.PropertyId, original.Purpose, new string('B', 64));
        var otherVersion = new ListingEvidenceReference(original.EvidenceId, Guid.NewGuid(), original.PropertyId, original.Purpose, original.Sha256);

        foreach (var wrong in new[] { otherDigest, otherVersion })
        {
            _provider.Respond([Assessment(wrong)]);

            AssertUnverified(await SubmissionEvidenceVerifier.VerifyAsync(draft, _provider, Now));
        }
    }

    [Fact]
    public async Task AProviderFailurePropagatesAndIsNeverAnAnswer()
    {
        var draft = DraftWithEvidence(1);
        _provider.ExceptionToThrow = new InvalidOperationException("scanner unavailable");

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => SubmissionEvidenceVerifier.VerifyAsync(draft, _provider, Now));

        Assert.Equal("scanner unavailable", exception.Message);
    }

    [Fact]
    public void NoInputToTheVerifierCanCarryAVerdict()
    {
        // The only inputs are the draft, the trusted provider, the time and a token.
        var method = typeof(SubmissionEvidenceVerifier).GetMethod("VerifyAsync", BindingFlags.Public | BindingFlags.Static)!;

        Assert.Equal(
            new[] { typeof(ListingSubmission), typeof(IEvidenceAssessmentProvider), typeof(DateTimeOffset), typeof(CancellationToken) },
            method.GetParameters().Select(parameter => parameter.ParameterType).ToArray());
    }

    [Fact]
    public async Task ArgumentsAreRequired()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(() => SubmissionEvidenceVerifier.VerifyAsync(null!, _provider, Now));
        await Assert.ThrowsAsync<ArgumentNullException>(() => SubmissionEvidenceVerifier.VerifyAsync(DraftWithEvidence(1), null!, Now));
    }

    private static void AssertUnverified(Result<IReadOnlyList<ListingEvidenceAssessment>> result)
    {
        Assert.True(result.IsFailure);
        Assert.Same(WriteErrors.PrerequisitesUnverified, result.Error);
        Assert.Equal("listing_submissions.prerequisites_unverified", result.Error!.Code);
        Assert.Equal(ErrorType.Conflict, result.Error.Type);
    }

    private static ListingEvidenceReference Reference()
    {
        return new ListingEvidenceReference(Guid.NewGuid(), Guid.NewGuid(), PropertyId, ListingPurpose.Sale, Digest);
    }

    private static ListingEvidenceAssessment Assessment(
        ListingEvidenceReference reference,
        EvidenceScanStatus status = EvidenceScanStatus.Clean,
        DateTimeOffset? assessedAt = null)
    {
        return new ListingEvidenceAssessment(Guid.NewGuid(), reference, status, assessedAt ?? Now.AddMinutes(-5));
    }

    private static ListingSubmission DraftWithEvidence(int count)
    {
        var listing = new PropertyListing(Guid.NewGuid(), PropertyId, ListingPurpose.Sale, Created.AddDays(-1));
        var content = new ListingSubmissionContent(
            "SA", "Riyadh Province", "Riyadh", "Al Olaya", "Title", "Description", PropertyType.Unit, ListingPurpose.Sale, 1000m, "SAR", null);
        var authority = new ListingLegalAuthority(
            PropertyId,
            ListingPurpose.Sale,
            new ListingOwnerIdentity(ListingOwnerKind.Individual, Guid.NewGuid(), UserId),
            UserId,
            ListingRepresentationKind.Owner,
            workspaceId: null,
            Created.AddDays(-30),
            Created.AddYears(1));
        var evidence = Enumerable.Range(0, count).Select(_ => Reference()).ToArray();

        return new ListingSubmission(Guid.NewGuid(), listing, content, authority, evidence, Created);
    }

    // Reaches the state the Domain will allow once a draft may be created without evidence, without changing the
    // Domain: the persistence constructor, then the evidence list emptied.
    private static ListingSubmission DraftWithoutEvidence()
    {
        var draft = (ListingSubmission)Activator.CreateInstance(typeof(ListingSubmission), nonPublic: true)!;
        typeof(ListingSubmission).GetProperty(nameof(ListingSubmission.Evidence))!.SetValue(draft, Array.AsReadOnly(Array.Empty<ListingEvidenceReference>()));

        return draft;
    }

    private sealed class FakeEvidenceAssessmentProvider : IEvidenceAssessmentProvider
    {
        private EvidenceAssessmentResult _result = EvidenceAssessmentResult.Unavailable();

        public Exception? ExceptionToThrow { get; set; }

        public int CallCount { get; private set; }

        public IReadOnlyCollection<ListingEvidenceReference>? AskedAbout { get; private set; }

        public DateTimeOffset? AskedAt { get; private set; }

        public CancellationToken CancellationToken { get; private set; }

        public void Respond(IReadOnlyList<ListingEvidenceAssessment> assessments)
        {
            _result = EvidenceAssessmentResult.Assessed(assessments);
        }

        public void Unavailable()
        {
            _result = EvidenceAssessmentResult.Unavailable();
        }

        public Task<EvidenceAssessmentResult> AssessAsync(
            IReadOnlyCollection<ListingEvidenceReference> evidence,
            DateTimeOffset at,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            AskedAbout = evidence;
            AskedAt = at;
            CancellationToken = cancellationToken;

            if (ExceptionToThrow is not null)
            {
                throw ExceptionToThrow;
            }

            return Task.FromResult(_result);
        }

        public Task<EvidenceResolution> ResolveAsync(
            Guid actingUserId,
            Guid propertyId,
            ListingPurpose purpose,
            IReadOnlyCollection<Guid> evidenceIds,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }
    }
}
