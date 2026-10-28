using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using SmartProperty.Application.Abstractions.Marketplace;
using SmartProperty.Domain.Marketplace;
using SmartProperty.Persistence.Context;
using SmartProperty.Persistence.Entities.Marketplace;
using SmartProperty.Persistence.Migrations;
using Xunit;

namespace SmartProperty.Persistence.IntegrationTests.Marketplace;

// No fixture or connection: SaveChanges is suppressed after the application's own guards.
public sealed class ListingDraftDiscardAuditMappingTests
{
    private static ApplicationDbContext Context() => new(new DbContextOptionsBuilder<ApplicationDbContext>()
        .UseNpgsql().AddInterceptors(new SuppressSave()).Options);

    private static ListingSubmission Draft()
    {
        var template = MarketplaceGraph.Create().SaleSubmission;
        var listing = new PropertyListing(Guid.NewGuid(), template.PropertyId, template.Purpose, MarketplaceGraph.Time);
        return new ListingSubmission(Guid.NewGuid(), listing, template.Content, template.Authority, [], MarketplaceGraph.Time);
    }

    private static ListingDraftDiscardedAuditEvent Event(ListingSubmission draft, string? fault = null) =>
        new(Guid.NewGuid(), fault == "time" ? draft.UpdatedAt.AddSeconds(-1) : draft.UpdatedAt,
            fault == "actor" ? Guid.NewGuid() : draft.Authority.ActingUserId,
            fault == "submission" ? Guid.NewGuid() : draft.Id,
            fault == "listing" ? Guid.NewGuid() : draft.ListingId,
            fault == "property" ? Guid.NewGuid() : draft.PropertyId,
            fault == "purpose" ? ListingPurpose.Rent : draft.Purpose,
            fault == "version" ? draft.Version + 1 : draft.Version, false);

    [Fact]
    public void AuditHasOnlyScalarFactsAndNoRelationshipsToDeletedRows()
    {
        using var context = Context();
        var model = context.Model.FindEntityType(typeof(ListingDraftDiscardAudit))!;
        Assert.Empty(model.GetForeignKeys());
        Assert.Empty(model.GetNavigations());
        Assert.Equal(new[] { "ActorUserId", "EventId", "ListingDeleted", "ListingId", "OccurredAt", "PropertyId", "Purpose", "Revision", "SubmissionId", "Version" },
            model.GetProperties().Select(x => x.Name).OrderBy(x => x).ToArray());
        Assert.Contains(model.GetIndexes(), x => x.IsUnique && x.Properties.Single().Name == "SubmissionId");
        var draft = Draft();
        var audit = ListingDraftDiscardAudit.Prepare(draft, 1, Event(draft));
        Assert.Equal(draft.Revision, audit.Revision);
        Assert.Equal(ListingSubmissionStatus.Draft, draft.Status);
        Assert.Null(draft.SubmittedAt);
    }

    [Theory]
    [InlineData("actor")]
    [InlineData("submission")]
    [InlineData("listing")]
    [InlineData("property")]
    [InlineData("purpose")]
    [InlineData("version")]
    [InlineData("time")]
    [InlineData("revision")]
    public void AuditPreparationRejectsInconsistentFacts(string fault)
    {
        var draft = Draft();
        Assert.Throws<InvalidOperationException>(() => ListingDraftDiscardAudit.Prepare(draft,
            fault == "revision" ? 2 : 1, Event(draft, fault)));
        Assert.Equal(1, draft.Revision);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task NoSaveOverloadAllowsStandaloneAuditOrDraftDeletion(int overload)
    {
        using var context = Context();
        var draft = Draft();
        context.Attach(draft);
        context.Add(ListingDraftDiscardAudit.Prepare(draft, 1, Event(draft)));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Save(context, overload));
        context.ChangeTracker.Clear();
        context.Attach(draft);
        context.Remove(draft);
        await Assert.ThrowsAsync<InvalidOperationException>(() => Save(context, overload));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AuditCannotBeChangedOrDeleted(bool delete)
    {
        using var context = Context();
        var draft = Draft();
        var audit = ListingDraftDiscardAudit.Prepare(draft, 1, Event(draft));
        context.Attach(audit);
        if (delete) context.Remove(audit);
        else context.Entry(audit).Property(x => x.ListingDeleted).CurrentValue = true;
        var error = Assert.Throws<InvalidOperationException>(() => context.SaveChanges());
        Assert.Equal("Draft discard audit history is append-only.", error.Message);
    }

    [Fact]
    public void MigrationOnlyAddsAuditStorageAndGuardsItsRollback()
    {
        var migration = new AddListingDraftDiscardAudit();
        var table = Assert.Single(migration.UpOperations.OfType<CreateTableOperation>());
        Assert.Equal("draft_discard_audits", table.Name);
        Assert.Equal("marketplace", table.Schema);
        Assert.Empty(table.ForeignKeys);
        Assert.All(migration.UpOperations, op => Assert.True(op is CreateTableOperation or CreateIndexOperation or SqlOperation));
        var appendOnly = Assert.Single(migration.UpOperations.OfType<SqlOperation>());
        Assert.Contains("BEFORE UPDATE OR DELETE OR TRUNCATE", appendOnly.Sql);
        Assert.False(appendOnly.SuppressTransaction);
        var guard = Assert.IsType<SqlOperation>(migration.DownOperations[0]);
        Assert.Contains("ACCESS EXCLUSIVE", guard.Sql);
        Assert.Contains("IF EXISTS", guard.Sql);
        Assert.False(guard.SuppressTransaction);
        Assert.IsType<DropTableOperation>(migration.DownOperations[1]);
    }

    [Fact]
    public void PublishedHistoryCannotProduceDiscardAudit()
    {
        var graph = MarketplaceGraph.Create();
        var publishedId = graph.Sale.PublishedSubmissionId;
        var submitted = graph.SaleSubmission;
        Assert.Throws<InvalidOperationException>(() => ListingDraftDiscardAudit.Prepare(submitted, submitted.Revision, Event(submitted)));
        Assert.Equal(publishedId, graph.Sale.PublishedSubmissionId);
        Assert.NotNull(submitted.ReviewDecision);
    }

    private static async Task Save(ApplicationDbContext context, int overload)
    {
        switch (overload)
        {
            case 0: context.SaveChanges(); break;
            case 1: context.SaveChanges(false); break;
            case 2: await context.SaveChangesAsync(); break;
            default: await context.SaveChangesAsync(false); break;
        }
    }

    private sealed class SuppressSave : SaveChangesInterceptor
    {
        public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result) => InterceptionResult<int>.SuppressWithResult(0);
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default) => ValueTask.FromResult(InterceptionResult<int>.SuppressWithResult(0));
    }
}
