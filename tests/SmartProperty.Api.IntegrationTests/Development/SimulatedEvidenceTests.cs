using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SmartProperty.Api.Infrastructure.Development;
using SmartProperty.Domain.Marketplace;
using Xunit;

namespace SmartProperty.Api.IntegrationTests.Development;

/// <summary>
/// The simulated assessment recognises only fixtures registered through the store. Unknown ids, another user's fixtures,
/// another property's fixtures, altered files and rejected content all fail; nothing is ever clean by default.
/// </summary>
public sealed class SimulatedEvidenceTests : IDisposable
{
    private static readonly Guid Owner = Guid.NewGuid();
    private static readonly Guid Stranger = Guid.NewGuid();
    private static readonly Guid Property = Guid.NewGuid();
    private static readonly DateTimeOffset At = new(2026, 11, 10, 12, 0, 0, TimeSpan.Zero);

    private readonly string _root = Path.Combine(Path.GetTempPath(), "smartproperty-fixtures-" + Guid.NewGuid().ToString("N"));
    private readonly EvidenceFixtureStore _store;
    private readonly SimulatedEvidenceAssessmentProvider _provider;

    public SimulatedEvidenceTests()
    {
        _store = new EvidenceFixtureStore(Options.Create(new DemoModeOptions { Enabled = true, EvidenceRootPath = _root, MaxEvidenceBytes = 1024 }));
        _provider = new SimulatedEvidenceAssessmentProvider(_store, NullLogger<SimulatedEvidenceAssessmentProvider>.Instance);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public async Task ARegisteredFixtureResolvesForItsOwnerAndPropertyAndIsAssessedClean()
    {
        var fixture = await RegisterAsync("The title deed.");

        var resolution = await _provider.ResolveAsync(Owner, Property, ListingPurpose.Sale, [fixture.EvidenceId]);
        var reference = Assert.Single(resolution.References!);
        var assessed = await _provider.AssessAsync([reference], At);

        Assert.Equal(fixture.EvidenceId, reference.EvidenceId);
        Assert.Equal(fixture.Sha256, reference.Sha256);
        Assert.Equal(EvidenceScanStatus.Clean, Assert.Single(assessed.Assessments!).Status);
    }

    [Fact]
    public async Task AnUnknownIdIsNeverResolvedSoTheCallerRefusesTheDraft()
    {
        var known = await RegisterAsync("Real fixture.");

        var resolution = await _provider.ResolveAsync(Owner, Property, ListingPurpose.Sale, [known.EvidenceId, Guid.NewGuid()]);

        // Only the known one comes back; the draft handler requires every requested id to come back and refuses otherwise.
        Assert.Equal(known.EvidenceId, Assert.Single(resolution.References!).EvidenceId);
    }

    [Fact]
    public async Task AnotherUsersFixtureAndAnotherPropertysFixtureAreNotResolved()
    {
        var fixture = await RegisterAsync("Mine.");

        var otherUser = await _provider.ResolveAsync(Stranger, Property, ListingPurpose.Sale, [fixture.EvidenceId]);
        var otherProperty = await _provider.ResolveAsync(Owner, Guid.NewGuid(), ListingPurpose.Sale, [fixture.EvidenceId]);

        Assert.Empty(otherUser.References!);
        Assert.Empty(otherProperty.References!);
    }

    [Fact]
    public async Task AReferenceThatMatchesNoFixtureIsNotAssessedAtAll()
    {
        var invented = new ListingEvidenceReference(Guid.NewGuid(), Guid.NewGuid(), Property, ListingPurpose.Sale, new string('A', 64));

        var assessed = await _provider.AssessAsync([invented], At);

        Assert.Empty(assessed.Assessments!);
    }

    [Fact]
    public async Task AReferenceWithTheWrongDigestOrVersionIsNotAssessed()
    {
        var fixture = await RegisterAsync("Contract.");
        var wrongDigest = new ListingEvidenceReference(fixture.EvidenceId, fixture.VersionId, Property, ListingPurpose.Sale, new string('B', 64));
        var wrongVersion = new ListingEvidenceReference(fixture.EvidenceId, Guid.NewGuid(), Property, ListingPurpose.Sale, fixture.Sha256);

        var assessed = await _provider.AssessAsync([wrongDigest, wrongVersion], At);

        Assert.Empty(assessed.Assessments!);
    }

    [Fact]
    public async Task AFileAlteredAfterRegistrationFailsAndAMissingOneFailsToo()
    {
        var altered = await RegisterAsync("Original.");
        var missing = await RegisterAsync("Will vanish.");
        await File.WriteAllTextAsync(_store.FilePath(altered.EvidenceId), "Tampered.");
        File.Delete(_store.FilePath(missing.EvidenceId));

        var references = new[] { altered, missing }
            .Select(fixture => new ListingEvidenceReference(fixture.EvidenceId, fixture.VersionId, Property, ListingPurpose.Sale, fixture.Sha256))
            .ToArray();
        var assessed = await _provider.AssessAsync(references, At);

        Assert.All(assessed.Assessments!, assessment => Assert.Equal(EvidenceScanStatus.Failed, assessment.Status));
        Assert.Equal(2, assessed.Assessments!.Count);
    }

    [Fact]
    public async Task ContentCarryingTheRejectMarkerIsRejected()
    {
        var fixture = await RegisterAsync($"Looks fine {EvidenceFixtureStore.RejectMarker} but is not.");
        var reference = new ListingEvidenceReference(fixture.EvidenceId, fixture.VersionId, Property, ListingPurpose.Sale, fixture.Sha256);

        var assessed = await _provider.AssessAsync([reference], At);

        Assert.Equal(EvidenceScanStatus.Rejected, Assert.Single(assessed.Assessments!).Status);
    }

    [Fact]
    public async Task AnEmptyOrOversizedFileIsRefusedAndNothingIsKept()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() => RegisterAsync(string.Empty));
        await Assert.ThrowsAsync<InvalidOperationException>(() => RegisterAsync(new string('x', 2000)));

        Assert.Empty(Directory.GetFiles(_root, "*.bin"));
    }

    [Fact]
    public async Task AFixtureSurvivesARestartBecauseTheIndexIsOnDisk()
    {
        var fixture = await RegisterAsync("Persistent.");
        var restarted = new EvidenceFixtureStore(Options.Create(new DemoModeOptions { Enabled = true, EvidenceRootPath = _root }));

        var found = await restarted.FindAsync(fixture.EvidenceId, CancellationToken.None);

        Assert.Equal(fixture, found);
        Assert.Null(await restarted.FindAsync(Guid.NewGuid(), CancellationToken.None));
    }

    [Fact]
    public async Task TheFixtureLabelStatesThatNothingWasVerified()
    {
        Assert.Contains("SIMULATED", EvidenceFixture.Label, StringComparison.Ordinal);
        await RegisterAsync("x");
    }

    private async Task<EvidenceFixture> RegisterAsync(string text)
    {
        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(text));

        return await _store.RegisterAsync(Owner, Property, "deed.txt", "text/plain", stream, At, CancellationToken.None);
    }
}
