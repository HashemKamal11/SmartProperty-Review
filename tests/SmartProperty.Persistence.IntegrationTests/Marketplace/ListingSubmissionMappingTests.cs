using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using SmartProperty.Domain.Marketplace;
using SmartProperty.Persistence.Context;
using Xunit;

namespace SmartProperty.Persistence.IntegrationTests.Marketplace;

// No PostgreSQL fixture; saves are suppressed before SQL generation/execution.
public sealed class ListingSubmissionMappingTests
{
    private static ApplicationDbContext Context() => new(new DbContextOptionsBuilder<ApplicationDbContext>()
        .UseNpgsql().AddInterceptors(new SuppressSave()).Options);

    [Fact]
    public void SnapshotsRoundTripThroughConfiguredConverters()
    {
        using var context = Context();
        var graph = MarketplaceGraph.Create(false);
        var submission = graph.RentSubmission;
        foreach (var name in new[] { "Content", "Authority", "Evidence", "SubmittedAssessments" })
        {
            var property = context.Model.FindEntityType(typeof(ListingSubmission))!.FindProperty(name)!;
            var converter = property.GetTypeMapping().Converter!;
            var value = typeof(ListingSubmission).GetProperty(name)!.GetValue(submission);
            var json = Assert.IsType<string>(converter.ConvertToProvider(value));
            var restored = converter.ConvertFromProvider(json);
            Assert.NotSame(value, restored);
            Assert.True(property.GetValueComparer()!.Equals(value, restored));
        }
        var decision = submission.ReviewDecision!;
        foreach (var name in new[] { "Reviewer", "Assessments" })
        {
            var property = context.Model.FindEntityType(typeof(ListingReviewDecision))!.FindProperty(name)!;
            var converter = property.GetTypeMapping().Converter!;
            var value = typeof(ListingReviewDecision).GetProperty(name)!.GetValue(decision);
            Assert.True(property.GetValueComparer()!.Equals(value, converter.ConvertFromProvider(converter.ConvertToProvider(value))));
        }
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"FormatVersion\":2,\"Payload\":{}}")]
    [InlineData("{\"FormatVersion\":1,\"Payload\":{}}")]
    [InlineData("{\"FormatVersion\":1,\"FormatVersion\":1,\"Payload\":{}}")]
    [InlineData("null")]
    public void MalformedContentFailsClosed(string json)
    {
        using var context = Context();
        var converter = context.Model.FindEntityType(typeof(ListingSubmission))!.FindProperty("Content")!.GetTypeMapping().Converter!;
        Assert.NotNull(Record.Exception(() => converter.ConvertFromProvider(json)));
    }

    [Fact]
    public void RelationshipsArePurposeSafeAndRestrictive()
    {
        using var context = Context();
        var submission = context.Model.FindEntityType(typeof(ListingSubmission))!;
        Assert.Contains(submission.GetIndexes(), x => x.IsUnique && x.Properties.Select(p => p.Name).SequenceEqual(new[] { "ListingId", "Version" }));
        var listing = context.Model.FindEntityType(typeof(PropertyListing))!;
        Assert.Contains(listing.GetForeignKeys(), x => x.PrincipalEntityType.ClrType == typeof(ListingSubmission) && x.Properties.Count == 4);
        Assert.All(submission.GetForeignKeys(), x => Assert.Equal(DeleteBehavior.Restrict, x.DeleteBehavior));
        Assert.All(context.Model.FindEntityType(typeof(ListingReviewDecision))!.GetForeignKeys(), x => Assert.Equal(DeleteBehavior.Restrict, x.DeleteBehavior));
    }

    [Fact]
    public void ReviewedSubmissionRemovalIsRejectedByRequiredRelationship()
    {
        using var context = Context();
        var submission = MarketplaceGraph.Create().SaleSubmission;
        context.Attach(submission);
        Assert.NotNull(submission.ReviewDecision);
        Assert.Equal(EntityState.Unchanged, context.Entry(submission).State);

        var error = Assert.Throws<InvalidOperationException>(() => context.Remove(submission));

        Assert.Contains("association", error.Message);
        Assert.Contains(nameof(ListingSubmission), error.Message);
        Assert.Contains(nameof(ListingReviewDecision), error.Message);
    }

    [Fact]
    public async Task PendingSubmissionDeletionIsRejectedBySaveChangesGuard()
    {
        await using var context = Context();
        var listing = new PropertyListing(Guid.NewGuid(), Guid.NewGuid(), ListingPurpose.Sale, MarketplaceGraph.Time);
        var submission = MarketplaceGraph.Pending(listing, MarketplaceGraph.Time.AddSeconds(1));
        Assert.Equal(ListingSubmissionStatus.PendingReview, submission.Status);
        Assert.Null(submission.ReviewDecision);
        // Attach models an existing persisted row; this no-database test does not perform an insert.
        context.Attach(submission);
        Assert.Equal(EntityState.Unchanged, context.Entry(submission).State);
        context.Remove(submission);
        Assert.Equal(EntityState.Deleted, context.Entry(submission).State);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => context.SaveChangesAsync());

        Assert.Equal("Submission history cannot be deleted.", error.Message);
    }

    [Fact]
    public async Task ReviewDecisionDeletionIsRejectedBySaveChangesGuard()
    {
        await using var context = Context();
        var decision = MarketplaceGraph.Create().SaleSubmission.ReviewDecision!;
        context.Attach(decision);
        context.Remove(decision);
        Assert.Equal(EntityState.Deleted, context.Entry(decision).State);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => context.SaveChangesAsync());

        Assert.Equal("Historical review decisions are immutable.", error.Message);
    }

    [Fact]
    public void SnapshotReplacementIsRejectedBeforeSave()
    {
        using var context = Context();
        var graph = MarketplaceGraph.Create();
        context.Attach(graph.SaleSubmission);
        context.Entry(graph.SaleSubmission).Property(x => x.Content).CurrentValue = graph.RentSubmission.Content;
        Assert.Throws<InvalidOperationException>(() => context.SaveChanges());
    }

    [Fact]
    public void DecisionRewriteIsRejectedBeforeSave()
    {
        using var context = Context();
        var decision = MarketplaceGraph.Create().SaleSubmission.ReviewDecision!;
        context.Attach(decision);
        context.Entry(decision).Property(x => x.Approved).CurrentValue = false;
        Assert.Throws<InvalidOperationException>(() => context.SaveChanges(false));
    }

    private sealed class SuppressSave : SaveChangesInterceptor
    {
        public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result) => InterceptionResult<int>.SuppressWithResult(0);
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(InterceptionResult<int>.SuppressWithResult(0));
    }
}
