using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using SmartProperty.Domain.Marketplace;
using SmartProperty.Persistence.Context;
using Xunit;

namespace SmartProperty.Persistence.IntegrationTests.Marketplace;

// No database fixture. The interceptor suppresses every save before SQL execution.
public sealed class ListingWriteMappingTests
{
    private static ApplicationDbContext Context() => new(new DbContextOptionsBuilder<ApplicationDbContext>()
        .UseNpgsql().AddInterceptors(new SuppressSave()).Options);
    private static ListingSubmission Draft()
    {
        var template = MarketplaceGraph.Create().SaleSubmission;
        var listing = new PropertyListing(Guid.NewGuid(), template.PropertyId, template.Purpose, MarketplaceGraph.Time);
        return new ListingSubmission(Guid.NewGuid(), listing, template.Content, template.Authority, [], MarketplaceGraph.Time);
    }

    [Fact]
    public void RevisionIsConcurrencyTokenAndEvidenceCheckDistinguishesDrafts()
    {
        using var context = Context();
        var entity = context.GetService<IDesignTimeModel>().Model.FindEntityType(typeof(ListingSubmission))!;
        var revision = entity.FindProperty(nameof(ListingSubmission.Revision))!;
        Assert.True(revision.IsConcurrencyToken);
        Assert.Equal("revision", revision.GetColumnName());
        Assert.Contains(entity.GetCheckConstraints(), c => c.Name == "ck_marketplace_submissions_revision" && c.Sql == "revision > 0");
        var evidence = Assert.Single(entity.GetCheckConstraints(), c => c.Name == "ck_marketplace_submissions_evidence");
        Assert.Contains("(status = 'Draft' OR jsonb_array_length(evidence_snapshot->'Payload') > 0)", evidence.Sql);
        Assert.StartsWith("COALESCE(", evidence.Sql);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DraftEvidenceUpdatePassesSaveGuard(bool asynchronous)
    {
        using var context = Context();
        var draft = Draft();
        context.Attach(draft);
        var evidence = new ListingEvidenceReference(Guid.NewGuid(), Guid.NewGuid(), draft.PropertyId, draft.Purpose, new string('A', 64));
        draft.UpdateDraft(1, draft.Content, [evidence], MarketplaceGraph.Time.AddMinutes(1));
        if (asynchronous) await context.SaveChangesAsync(); else context.SaveChanges();
        Assert.Equal(2, draft.Revision);
    }

    [Fact]
    public void DraftSnapshotReplacementWithoutRevisionIsRejected()
    {
        using var context = Context();
        var draft = Draft();
        context.Attach(draft);
        context.Entry(draft).Property(x => x.Evidence).CurrentValue =
            new[] { new ListingEvidenceReference(Guid.NewGuid(), Guid.NewGuid(), draft.PropertyId, draft.Purpose, new string('A', 64)) };
        Assert.Throws<InvalidOperationException>(() => context.SaveChanges());
    }

    [Fact]
    public void DraftDeletionRemainsBlockedUntilAtomicAuditPersistenceExists()
    {
        using var context = Context();
        var draft = Draft();
        draft.EnsureCanDiscard(1);
        context.Attach(draft);
        context.Remove(draft);
        Assert.Throws<InvalidOperationException>(() => context.SaveChanges());
    }

    private sealed class SuppressSave : SaveChangesInterceptor
    {
        public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result) => InterceptionResult<int>.SuppressWithResult(0);
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(InterceptionResult<int>.SuppressWithResult(0));
    }
}
