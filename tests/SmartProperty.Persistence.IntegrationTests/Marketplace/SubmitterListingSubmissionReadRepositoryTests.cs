using System.Collections.Concurrent;
using System.Data.Common;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using SmartProperty.Application.Abstractions.Persistence.Repositories;
using SmartProperty.Application.Authorization;
using SmartProperty.Application.Marketplace.ListingSubmissions;
using SmartProperty.Common.Pagination;
using SmartProperty.Domain.Identity;
using SmartProperty.Domain.Marketplace;
using SmartProperty.Domain.PropertyRegistry;
using SmartProperty.Domain.Workspaces;
using SmartProperty.Persistence.Context;
using SmartProperty.Persistence.IntegrationTests.Infrastructure;
using Xunit;

namespace SmartProperty.Persistence.IntegrationTests.Marketplace;

public sealed class SubmitterListingSubmissionReadRepositoryTests(PostgreSqlFixture fixture) : DatabaseTest(fixture)
{
    private static readonly Guid ActorA = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid ActorB = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
    private static readonly DateTimeOffset T0 = MarketplaceGraph.Time;
    private const string Rejection = "Authorized rejection reason";
    private readonly ReadObserver observer = new();

    private protected override IInterceptor[] ExtraInterceptors() => [observer];

    [Fact]
    public async Task BothActorsHaveIsolatedCountsPagesAndDetailsForEveryPurposeAndStatus()
    {
        var rows = new List<ListingSubmission>();
        foreach (var actor in new[] { ActorA, ActorB })
        foreach (var purpose in Enum.GetValues<ListingPurpose>())
        foreach (var status in Enum.GetValues<ListingSubmissionStatus>())
        {
            var seeded = await SeedAsync(actor, status, purpose, minute: rows.Count + 1);
            rows.AddRange(seeded.Rows);
        }

        foreach (var actor in new[] { ActorA, ActorB })
        foreach (var purpose in new ListingPurpose?[] { null, ListingPurpose.Sale, ListingPurpose.Rent })
        foreach (var status in new ListingSubmissionStatus?[]
            { null, ListingSubmissionStatus.Draft, ListingSubmissionStatus.PendingReview,
              ListingSubmissionStatus.Approved, ListingSubmissionStatus.Rejected, ListingSubmissionStatus.Superseded })
        {
            var expected = rows.Where(row => row.Authority.ActingUserId == actor
                && (purpose is null || row.Purpose == purpose) && (status is null || row.Status == status))
                .OrderByDescending(row => row.UpdatedAt)
                .ThenBy(row => row.Id.ToString("D"), StringComparer.Ordinal).Select(row => row.Id).ToArray();
            var seen = new List<Guid>();
            for (var skip = 0; skip <= expected.Length; skip += 2)
            {
                var page = await Read(r => r.ListBySubmitterAsync(actor, purpose, status, skip, 2));
                Assert.Equal((long)expected.Length, page.TotalCount);
                Assert.Equal(expected.Skip(skip).Take(2), page.Items.Select(item => item.SubmissionId));
                seen.AddRange(page.Items.Select(item => item.SubmissionId));
            }
            Assert.Equal(expected, seen);
        }

        foreach (var row in rows)
        {
            var owner = row.Authority.ActingUserId;
            var detail = await Read(r => r.GetBySubmitterAsync(owner, row.Id));
            Assert.NotNull(detail);
            Assert.Equal(row.Status, detail.Status);
            Assert.Null(await Read(r => r.GetBySubmitterAsync(owner == ActorA ? ActorB : ActorA, row.Id)));
        }
        Assert.Null(await Read(r => r.GetBySubmitterAsync(ActorA, Guid.NewGuid())));
        Assert.Null(await Read(r => r.GetBySubmitterAsync(ActorA, Guid.Empty)));
    }

    [Fact]
    public async Task LinkedOwnerWorkspaceMemberAndPlatformReviewerDoNotInheritActorAccess()
    {
        Guid workspaceId;
        Guid memberId;
        Guid adminId;
        await using (var context = Host.CreateVerificationContext())
        {
            var actor = new User(ActorA, "actor@example.test", "Acting", "User", T0);
            var owner = new User(ActorB, "owner@example.test", "Linked", "Owner", T0);
            var member = AccessGraph.ActiveUser("member@example.test");
            var workspace = AccessGraph.NewWorkspace();
            context.AddRange(actor, owner, member, workspace);
            context.AddRange(AccessGraph.NewMembership(actor.Id, workspace.Id),
                AccessGraph.NewMembership(member.Id, workspace.Id));
            var admin = await AccessGraph.SeedPlatformGrantAsync(context, PermissionCodes.MarketplaceSubmissionsReviewRead);
            workspaceId = workspace.Id;
            memberId = member.Id;
            adminId = admin.User.Id;
        }

        var seeded = await SeedAsync(ActorA, ListingSubmissionStatus.Draft,
            linkedOwner: ActorB, workspaceId: workspaceId);
        var detail = await Read(r => r.GetBySubmitterAsync(ActorA, seeded.Submission.Id));
        Assert.NotNull(detail);
        Assert.Null(detail.SubmittedAt);
        Assert.Null(detail.Decision);
        Assert.Equal(ListingRepresentationKind.WorkspaceRepresentative, detail.Representation);

        foreach (var nonActor in new[] { ActorB, memberId, adminId })
        {
            Assert.Null(await Read(r => r.GetBySubmitterAsync(nonActor, seeded.Submission.Id)));
            var page = await Read(r => r.ListBySubmitterAsync(nonActor, null, null, 0, 10));
            Assert.Empty(page.Items);
            Assert.Equal(0, page.TotalCount);
        }
    }

    [Fact]
    public async Task OrderingUsesUpdateTimeAndUuidAscendingAndAcceptsBoundedEmptyPages()
    {
        var older = await SeedAsync(ActorA, ListingSubmissionStatus.Draft, minute: 1);
        var tiedA = await SeedAsync(ActorA, ListingSubmissionStatus.Draft, minute: 2);
        var tiedB = await SeedAsync(ActorA, ListingSubmissionStatus.Draft, minute: 2);
        await using (var context = Host.CreateVerificationContext())
        {
            var draft = await context.ListingSubmissions.SingleAsync(row => row.Id == older.Submission.Id);
            draft.UpdateDraft(draft.Revision, draft.Content, draft.Evidence, T0.AddMinutes(3));
            await context.SaveChangesAsync();
        }

        var expected = new[] { older.Submission.Id }.Concat(
            new[] { tiedA.Submission.Id, tiedB.Submission.Id }.OrderBy(id => id.ToString("D"), StringComparer.Ordinal)).ToArray();
        var first = await Read(r => r.ListBySubmitterAsync(ActorA, null, null, 0, 2));
        var second = await Read(r => r.ListBySubmitterAsync(ActorA, null, null, 2, 2));
        Assert.Equal(expected, first.Items.Concat(second.Items).Select(item => item.SubmissionId));
        Assert.Equal(3, first.TotalCount);
        Assert.Equal(3, second.TotalCount);
        var end = await Read(r => r.ListBySubmitterAsync(ActorA, null, null, PageParameters.MaxOffset, PageParameters.MaxPageSize));
        Assert.Empty(end.Items);
        Assert.Equal(3, end.TotalCount);
        var empty = await Read(r => r.ListBySubmitterAsync(Guid.NewGuid(), null, null, 0, 10));
        Assert.Empty(empty.Items);
        Assert.Equal(0, empty.TotalCount);
    }

    [Fact]
    public async Task CurrentPropertyVocabularyAndRequiredLocationValuesRoundTrip()
    {
        foreach (var type in Enum.GetValues<PropertyType>())
        {
            var seeded = await SeedAsync(ActorA, ListingSubmissionStatus.PendingReview,
                ListingPurpose.Rent, propertyType: type);
            var detail = await Read(r => r.GetBySubmitterAsync(ActorA, seeded.Submission.Id));
            Assert.NotNull(detail);
            Assert.Equal(type, detail.PropertyType);
            Assert.Equal((125.25m, "EGP", RentPeriod.Monthly),
                (detail.Price.Amount, detail.Price.Currency, detail.Price.RentPeriod!.Value));
            Assert.Equal(("EG", "Region", "City", "District"),
                (detail.Location.CountryCode, detail.Location.Region, detail.Location.City, detail.Location.District));
            Assert.Equal("Description", detail.Description);
            Assert.Null(detail.Decision);
        }
        var page = await Read(r => r.ListBySubmitterAsync(ActorA, ListingPurpose.Rent, null, 0, 100));
        Assert.Equal(Enum.GetValues<PropertyType>().Order(), page.Items.Select(item => item.PropertyType).Order());
    }

    [Fact]
    public async Task SupersessionRetainsOriginalApprovalAndScopesVersionsIndividually()
    {
        var seeded = await SeedAsync(ActorA, ListingSubmissionStatus.Superseded, successorActor: ActorB);
        var first = await Read(r => r.GetBySubmitterAsync(ActorA, seeded.Submission.Id));
        var successor = await Read(r => r.GetBySubmitterAsync(ActorB, seeded.Successor!.Id));
        Assert.NotNull(first);
        Assert.NotNull(successor);
        Assert.Equal(ListingSubmissionStatus.Superseded, first.Status);
        Assert.True(first.Decision!.Approved);
        Assert.True(first.Decision.DecidedAt < first.UpdatedAt);
        Assert.Null(first.Decision.Reason);
        Assert.False(first.IsCurrentlyPublished);
        Assert.Equal(2, successor.Version);
        Assert.True(successor.IsCurrentlyPublished);
        Assert.Null(await Read(r => r.GetBySubmitterAsync(ActorA, successor.SubmissionId)));
        Assert.Null(await Read(r => r.GetBySubmitterAsync(ActorB, first.SubmissionId)));
        Assert.Equal(first.SubmissionId, Assert.Single(
            (await Read(r => r.ListBySubmitterAsync(ActorA, null, null, 0, 10))).Items).SubmissionId);
    }

    [Fact]
    public async Task WithdrawalRetainsApprovalAndPointerButClearsCurrentPublicationInBothReads()
    {
        var seeded = await SeedAsync(ActorA, ListingSubmissionStatus.Approved);
        Assert.True((await Read(r => r.GetBySubmitterAsync(ActorA, seeded.Submission.Id)))!.IsCurrentlyPublished);
        await using (var context = Host.CreateVerificationContext())
        {
            var listing = await context.PropertyListings.SingleAsync(row => row.Id == seeded.Listing.Id);
            listing.Withdraw(T0.AddHours(2));
            await context.SaveChangesAsync();
            Assert.Equal(seeded.Submission.Id, listing.PublishedSubmissionId);
        }

        var detail = await Read(r => r.GetBySubmitterAsync(ActorA, seeded.Submission.Id));
        Assert.NotNull(detail);
        Assert.Equal(ListingSubmissionStatus.Approved, detail.Status);
        Assert.True(detail.Decision!.Approved);
        Assert.False(detail.IsCurrentlyPublished);
        Assert.False(Assert.Single((await Read(r => r.ListBySubmitterAsync(ActorA, null, null, 0, 10))).Items).IsCurrentlyPublished);
    }

    [Fact]
    public async Task ReasonsAreSelectedOnlyForTheActorsRejectedDetailAndPrivateValuesNeverMaterialize()
    {
        var rejected = await SeedAsync(ActorA, ListingSubmissionStatus.Rejected, linkedOwner: ActorB,
            workspaceId: Guid.NewGuid());
        var approved = await SeedAsync(ActorA, ListingSubmissionStatus.Approved);
        const string privateApprovalNote = "SENTINEL-PRIVATE-APPROVAL-NOTE";
        await using (var context = Host.CreateVerificationContext())
        {
            await context.Database.ExecuteSqlRawAsync(
                "UPDATE marketplace.review_decisions SET reason = {0} WHERE submission_id = {1}",
                privateApprovalNote, approved.Submission.Id);
        }

        observer.Clear();
        var page = await Read(r => r.ListBySubmitterAsync(ActorA, null, null, 0, 10));
        var pageSql = observer.Commands.ToArray();
        Assert.Equal(2, pageSql.Length);
        Assert.All(pageSql, command =>
        {
            Assert.DoesNotContain("d.reason", command.Sql);
            Assert.DoesNotContain("review_decisions", command.Sql);
            Assert.DoesNotContain("Description", command.Sql);
        });
        Assert.Equal(SubmitterListingSubmissionReadQueryTests.SummaryColumns.Order(),
            observer.Columns.Last().Order());

        observer.Clear();
        var detail = await Read(r => r.GetBySubmitterAsync(ActorA, rejected.Submission.Id));
        Assert.NotNull(detail);
        Assert.False(detail.Decision!.Approved);
        Assert.Equal(Rejection, detail.Decision.Reason);
        Assert.Equal(SubmitterListingSubmissionReadQueryTests.DetailColumns.Order(),
            Assert.Single(observer.Columns).Order());
        var approvedDetail = await Read(r => r.GetBySubmitterAsync(ActorA, approved.Submission.Id));
        Assert.Null(approvedDetail!.Decision!.Reason);
        Assert.Null(await Read(r => r.GetBySubmitterAsync(ActorB, rejected.Submission.Id)));

        var json = JsonSerializer.Serialize(new { page, detail, approvedDetail });
        foreach (var submission in new[] { rejected.Submission, approved.Submission })
        {
            foreach (var secret in new[]
            {
                submission.PropertyId.ToString(), submission.Authority.ActingUserId.ToString(),
                submission.Authority.DeclaredOwner.LegalPartyReferenceId.ToString(),
                submission.Authority.DeclaredOwner.LinkedUserId!.Value.ToString(),
                submission.Evidence[0].EvidenceId.ToString(), submission.Evidence[0].VersionId.ToString(),
                submission.Evidence[0].Sha256, submission.ReviewDecision!.Reviewer.ReviewerUserId.ToString(),
                submission.Authority.WorkspaceId?.ToString() ?? "UNUSED-WORKSPACE-SENTINEL", privateApprovalNote
            })
                Assert.DoesNotContain(secret, json, StringComparison.OrdinalIgnoreCase);
        }
        Assert.DoesNotContain(Rejection, JsonSerializer.Serialize(page));
        Assert.DoesNotContain(privateApprovalNote, JsonSerializer.Serialize(approvedDetail));
        Assert.All(observer.Commands, command =>
        {
            Assert.DoesNotContain("reviewer_snapshot", command.Sql);
            Assert.DoesNotContain("evidence_snapshot", command.Sql);
            Assert.DoesNotContain("assessment", command.Sql, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("DeclaredOwner", command.Sql);
            Assert.DoesNotContain("WorkspaceId", command.Sql);
        });
    }

    [Fact]
    public async Task ExecutedQueriesAreParameterizedBoundedUntrackedReadOnlyAndHaveNoNPlusOne()
    {
        for (var i = 0; i < 4; i++)
            await SeedAsync(ActorA, ListingSubmissionStatus.Draft, ListingPurpose.Rent, minute: i + 1);

        using var scope = Host.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var repository = scope.ServiceProvider.GetRequiredService<ISubmitterListingSubmissionReadRepository>();
        await using var transaction = await context.Database.BeginTransactionAsync();
        await context.Database.ExecuteSqlRawAsync("SET TRANSACTION READ ONLY");
        Host.Commands.Reset();
        observer.Clear();

        var page = await repository.ListBySubmitterAsync(ActorA, ListingPurpose.Rent, ListingSubmissionStatus.Draft, 1, 2);
        Assert.Equal(2, page.Items.Count);
        Assert.Equal(4, page.TotalCount);
        Assert.Equal(2, Host.Commands.Count);
        var commands = observer.Commands.ToArray();
        Assert.Equal(2, commands.Length);
        Assert.All(commands, command =>
        {
            Assert.StartsWith("SELECT", command.Sql.TrimStart(), StringComparison.OrdinalIgnoreCase);
            Assert.Contains(ActorA.ToString("D"), command.Parameters);
            Assert.Contains("Rent", command.Parameters);
            Assert.Contains("Draft", command.Parameters);
            Assert.DoesNotContain(ActorA.ToString("D"), command.Sql);
            Assert.Contains("ActingUserId", command.Sql);
        });
        var paged = commands[1];
        Assert.Contains("LIMIT", paged.Sql);
        Assert.Contains("OFFSET", paged.Sql);
        Assert.Contains("ORDER BY", paged.Sql);
        Assert.Contains("\"UpdatedAt\" DESC", paged.Sql);
        Assert.Contains(1, paged.Parameters);
        Assert.Contains(2, paged.Parameters);

        observer.Clear();
        Host.Commands.Reset();
        var id = page.Items[0].SubmissionId;
        Assert.NotNull(await repository.GetBySubmitterAsync(ActorA, id));
        Assert.Equal(1, Host.Commands.Count);
        var single = Assert.Single(observer.Commands);
        Assert.Contains(ActorA.ToString("D"), single.Parameters);
        Assert.Contains(id, single.Parameters);
        Assert.DoesNotContain(id.ToString(), single.Sql);
        Assert.Contains("s.id =", single.Sql);
        Assert.Empty(context.ChangeTracker.Entries());
        await transaction.RollbackAsync();
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("null")]
    [InlineData("object")]
    [InlineData("array")]
    [InlineData("number")]
    [InlineData("invalid")]
    [InlineData("empty")]
    [InlineData("zero")]
    [InlineData("uppercase")]
    [InlineData("compact")]
    [InlineData("spaces")]
    [InlineData("injection")]
    public async Task NonCanonicalOrMalformedActorIdentityNeverMatches(string mutation)
    {
        var seeded = await SeedAsync(ActorA, ListingSubmissionStatus.Draft, linkedOwner: ActorB);
        object? value = mutation switch
        {
            "null" => null,
            "object" => new { ActingUserId = ActorA },
            "array" => new[] { ActorA },
            "number" => 1,
            "invalid" => "not-a-guid",
            "empty" => "",
            "zero" => Guid.Empty.ToString(),
            "uppercase" => ActorA.ToString().ToUpperInvariant(),
            "compact" => ActorA.ToString("N"),
            "spaces" => " " + ActorA + " ",
            "injection" => "' OR TRUE --",
            _ => null
        };
        await using (var context = Host.CreateVerificationContext())
        {
            if (mutation == "missing")
                await context.Database.ExecuteSqlRawAsync(
                    "UPDATE marketplace.submissions SET authority_snapshot = authority_snapshot #- ARRAY['Payload','ActingUserId'] WHERE id = {0}",
                    seeded.Submission.Id);
            else
                await SetAuthorityMember(context, seeded.Submission.Id, "ActingUserId", JsonSerializer.Serialize(value));
        }
        foreach (var actor in new[] { ActorA, ActorB })
        {
            Assert.Null(await Read(r => r.GetBySubmitterAsync(actor, seeded.Submission.Id)));
            var page = await Read(r => r.ListBySubmitterAsync(actor, null, null, 0, 10));
            Assert.Empty(page.Items);
            Assert.Equal(0, page.TotalCount);
        }
    }

    [Theory]
    [InlineData("null")]
    [InlineData("99")]
    [InlineData("\"1\"")]
    [InlineData("\"SENTINEL-INVALID-REPRESENTATION\"")]
    [InlineData("{}")]
    [InlineData("[]")]
    public async Task CorruptSelectedRepresentationFailsSafelyInsteadOfReturningGuessedValues(string json)
    {
        var seeded = await SeedAsync(ActorA, ListingSubmissionStatus.Draft);
        await using (var context = Host.CreateVerificationContext())
            await SetAuthorityMember(context, seeded.Submission.Id, "Representation", json);

        var detailError = await Assert.ThrowsAsync<InvalidOperationException>(
            () => Read(r => r.GetBySubmitterAsync(ActorA, seeded.Submission.Id)));
        var listError = await Assert.ThrowsAsync<InvalidOperationException>(
            () => Read(r => r.ListBySubmitterAsync(ActorA, null, null, 0, 10)));
        Assert.Equal("A stored submitter read value is invalid.", detailError.Message);
        Assert.Equal(detailError.Message, listError.Message);
        // A damaged private row must not be selected for a different actor.
        Assert.Null(await Read(r => r.GetBySubmitterAsync(ActorB, seeded.Submission.Id)));
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("opposite")]
    [InlineData("time")]
    public async Task CorruptHistoricalDecisionFailsWithoutDisclosingStoredValues(string mutation)
    {
        var seeded = await SeedAsync(ActorA, ListingSubmissionStatus.Approved);
        await using (var context = Host.CreateVerificationContext())
        {
            if (mutation == "missing")
                await context.Database.ExecuteSqlRawAsync(
                    "DELETE FROM marketplace.review_decisions WHERE submission_id = {0}", seeded.Submission.Id);
            else if (mutation == "opposite")
                await context.Database.ExecuteSqlRawAsync(
                    "UPDATE marketplace.review_decisions SET approved = false, reason = {0} WHERE submission_id = {1}",
                    "SENTINEL-WRONG-DECISION", seeded.Submission.Id);
            else
                await context.Database.ExecuteSqlRawAsync(
                    "UPDATE marketplace.review_decisions SET decided_at = {0} WHERE submission_id = {1}",
                    T0.AddYears(1), seeded.Submission.Id);
        }
        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => Read(r => r.GetBySubmitterAsync(ActorA, seeded.Submission.Id)));
        Assert.Equal("A stored submitter read value is invalid.", error.Message);
        Assert.Null(await Read(r => r.GetBySubmitterAsync(ActorB, seeded.Submission.Id)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DatabaseFailuresPropagateRatherThanBecomingEmptyOrNotFound(bool detail)
    {
        await using var broken = PersistenceTestHost.Create(Fixture.BuildMissingDatabaseConnectionString());
        using var scope = broken.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<ISubmitterListingSubmissionReadRepository>();
        await Assert.ThrowsAnyAsync<NpgsqlException>(async () =>
        {
            if (detail)
                await repository.GetBySubmitterAsync(ActorA, Guid.NewGuid());
            else
                await repository.ListBySubmitterAsync(ActorA, null, null, 0, 10);
        });
    }

    [Theory]
    [InlineData("insert")]
    [InlineData("submit")]
    [InlineData("delete")]
    public async Task CommitBetweenCountAndPageMayChangeCountButNeverActorScope(string change)
    {
        var seeded = await SeedAsync(ActorA, ListingSubmissionStatus.Draft);
        var gate = new ReadGate(sql => sql.Contains("LIMIT", StringComparison.Ordinal));
        observer.Gate = gate;
        var pending = Read(r => r.ListBySubmitterAsync(ActorA, null, ListingSubmissionStatus.Draft, 0, 10));
        try
        {
            await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(15));
            await SeedAsync(ActorB, ListingSubmissionStatus.Draft, minute: 20);
            if (change == "insert")
                await SeedAsync(ActorA, ListingSubmissionStatus.Draft, minute: 21);
            else
            {
                await using var context = Host.CreateVerificationContext();
                var submission = await context.ListingSubmissions.SingleAsync(row => row.Id == seeded.Submission.Id);
                if (change == "delete")
                    await context.Database.ExecuteSqlRawAsync(
                        "DELETE FROM marketplace.submissions WHERE id = {0} AND status = 'Draft'", submission.Id);
                else
                    Submit(submission, T0.AddMinutes(10));
                await context.SaveChangesAsync();
            }
        }
        finally
        {
            gate.Release.TrySetResult();
        }

        var page = await pending.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.Equal(1, page.TotalCount);
        Assert.Equal(change == "insert" ? 2 : 0, page.Items.Count);
        foreach (var item in page.Items)
            Assert.NotNull(await Read(r => r.GetBySubmitterAsync(ActorA, item.SubmissionId)));
    }

    [Fact]
    public async Task UncommittedReviewIsInvisibleAndCommittedDetailIsCoherent()
    {
        var seeded = await SeedAsync(ActorA, ListingSubmissionStatus.PendingReview);
        await using var writer = Host.CreateVerificationContext();
        await using var transaction = await writer.Database.BeginTransactionAsync();
        var submission = await writer.ListingSubmissions.SingleAsync(row => row.Id == seeded.Submission.Id);
        var listing = await writer.PropertyListings.SingleAsync(row => row.Id == seeded.Listing.Id);
        Approve(submission, listing, null, T0.AddMinutes(10));
        await writer.SaveChangesAsync();

        var before = await Read(r => r.GetBySubmitterAsync(ActorA, submission.Id));
        Assert.NotNull(before);
        Assert.Equal(ListingSubmissionStatus.PendingReview, before.Status);
        Assert.Null(before.Decision);
        Assert.False(before.IsCurrentlyPublished);
        await transaction.CommitAsync();

        var after = await Read(r => r.GetBySubmitterAsync(ActorA, submission.Id));
        Assert.NotNull(after);
        Assert.Equal(ListingSubmissionStatus.Approved, after.Status);
        Assert.True(after.Decision!.Approved);
        Assert.Equal(after.UpdatedAt, after.Decision.DecidedAt);
        Assert.True(after.IsCurrentlyPublished);
    }

    [Fact]
    public async Task ConcurrentScopesDoNotShareActorIdentity()
    {
        var a = await SeedAsync(ActorA, ListingSubmissionStatus.Draft);
        var b = await SeedAsync(ActorB, ListingSubmissionStatus.Draft);
        await Task.WhenAll(Enumerable.Range(0, 8).Select(async index =>
        {
            var actor = index % 2 == 0 ? ActorA : ActorB;
            var expected = index % 2 == 0 ? a.Submission.Id : b.Submission.Id;
            var page = await Read(r => r.ListBySubmitterAsync(actor, null, null, 0, 10));
            Assert.Equal(expected, Assert.Single(page.Items).SubmissionId);
            Assert.Equal(1, page.TotalCount);
        }));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellationInterruptsAQueryActuallyWaitingInPostgreSql(bool detail)
    {
        var seeded = await SeedAsync(ActorA, ListingSubmissionStatus.Draft);
        await using var blocker = new NpgsqlConnection(Host.ConnectionString);
        await blocker.OpenAsync();
        await using var transaction = await blocker.BeginTransactionAsync();
        await using (var command = new NpgsqlCommand(
            "LOCK TABLE marketplace.submissions IN ACCESS EXCLUSIVE MODE", blocker, transaction))
            await command.ExecuteNonQueryAsync();

        observer.Clear();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var pending = detail
            ? Read(async r => (object?)await r.GetBySubmitterAsync(ActorA, seeded.Submission.Id, cancellation.Token))
            : Read(async r => (object?)await r.ListBySubmitterAsync(ActorA, null, null, 0, 10, cancellation.Token));
        try
        {
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (observer.Commands.IsEmpty && DateTime.UtcNow < deadline)
                await Task.Delay(25);

            Assert.False(observer.Commands.IsEmpty, "The read must be sent to PostgreSQL.");
            Assert.False(pending.IsCompleted, "The read must still be waiting on the test-owned table lock.");
            await cancellation.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await pending);
        }
        finally
        {
            await cancellation.CancelAsync();
            await transaction.RollbackAsync();
        }
    }

    private async Task<T> Read<T>(Func<ISubmitterListingSubmissionReadRepository, Task<T>> action)
    {
        using var scope = Host.CreateScope();
        var result = await action(scope.ServiceProvider.GetRequiredService<ISubmitterListingSubmissionReadRepository>());
        Assert.Empty(scope.ServiceProvider.GetRequiredService<ApplicationDbContext>().ChangeTracker.Entries());
        return result;
    }

    private static Task<int> SetAuthorityMember(ApplicationDbContext context, Guid id, string member, string json) =>
        context.Database.ExecuteSqlRawAsync(
            "UPDATE marketplace.submissions SET authority_snapshot = " +
            "jsonb_set(authority_snapshot, ARRAY['Payload', {0}], CAST({1} AS jsonb)) WHERE id = {2}",
            member, json, id);

    private async Task<Seeded> SeedAsync(
        Guid actor, ListingSubmissionStatus status, ListingPurpose purpose = ListingPurpose.Sale,
        int minute = 1, PropertyType propertyType = PropertyType.Apartment,
        Guid? linkedOwner = null, Guid? workspaceId = null, Guid? successorActor = null)
    {
        await using var context = Host.CreateVerificationContext();
        var property = new Property(Guid.NewGuid(), propertyType, new PropertyAddress("EG"), T0);
        var listing = new PropertyListing(Guid.NewGuid(), property.Id, purpose, T0);
        context.AddRange(property, listing);
        await context.SaveChangesAsync();
        var at = T0.AddMinutes(minute);
        var submission = Build(listing, actor, propertyType, at, linkedOwner, workspaceId);
        if (status != ListingSubmissionStatus.Draft)
            Submit(submission, at);
        context.Add(submission);
        await context.SaveChangesAsync();
        if (status is ListingSubmissionStatus.Approved or ListingSubmissionStatus.Superseded)
            Approve(submission, listing, null, at.AddSeconds(10));
        else if (status == ListingSubmissionStatus.Rejected)
            submission.Reject(Guid.NewGuid(), MarketplaceGraph.Reviewer(submission), Rejection, at.AddSeconds(10));
        await context.SaveChangesAsync();

        ListingSubmission? successor = null;
        if (status == ListingSubmissionStatus.Superseded)
        {
            successor = Build(listing, successorActor ?? actor, propertyType, at.AddSeconds(20), predecessor: submission);
            Submit(successor, at.AddSeconds(20));
            context.Add(successor);
            await context.SaveChangesAsync();
            Approve(successor, listing, submission, at.AddSeconds(30));
            await context.SaveChangesAsync();
        }
        return new Seeded(submission, listing, successor);
    }

    private static ListingSubmission Build(PropertyListing listing, Guid actor, PropertyType type,
        DateTimeOffset at, Guid? linkedOwner = null, Guid? workspaceId = null, ListingSubmission? predecessor = null)
    {
        var owner = new ListingOwnerIdentity(ListingOwnerKind.Individual, Guid.NewGuid(), linkedOwner ?? actor);
        var representation = workspaceId.HasValue ? ListingRepresentationKind.WorkspaceRepresentative
            : linkedOwner.HasValue && linkedOwner != actor ? ListingRepresentationKind.IndividualRepresentative
            : ListingRepresentationKind.Owner;
        var authority = new ListingLegalAuthority(listing.PropertyId, listing.Purpose, owner, actor,
            representation, workspaceId, T0, null);
        var evidence = new ListingEvidenceReference(Guid.NewGuid(), Guid.NewGuid(), listing.PropertyId,
            listing.Purpose, new string('A', 64));
        var content = new ListingSubmissionContent("EG", "Region", "City", "District", "Title", "Description",
            type, listing.Purpose, 125.25m, "EGP", listing.Purpose == ListingPurpose.Rent ? RentPeriod.Monthly : null);
        return new ListingSubmission(Guid.NewGuid(), listing, content, authority, [evidence], at, predecessor);
    }

    private static void Submit(ListingSubmission submission, DateTimeOffset at) =>
        submission.Submit(submission.Authority.ActingUserId, submission.Authority.WorkspaceId, at,
            MarketplaceGraph.Assessments(submission, at), true, true);

    private static void Approve(ListingSubmission submission, PropertyListing listing,
        ListingSubmission? previous, DateTimeOffset at) =>
        submission.ApproveAndPublish(Guid.NewGuid(), listing, previous, MarketplaceGraph.Reviewer(submission), at,
            true, MarketplaceGraph.Assessments(submission, at), true, true);

    private sealed record Seeded(ListingSubmission Submission, PropertyListing Listing, ListingSubmission? Successor)
    {
        public IEnumerable<ListingSubmission> Rows => Successor is { } next ? [Submission, next] : [Submission];
    }

    private sealed record ObservedCommand(string Sql, object[] Parameters);

    private sealed class ReadGate(Func<string, bool> matches)
    {
        private int used;
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task BeforeAsync(string sql, CancellationToken cancellationToken)
        {
            if (!matches(sql) || Interlocked.CompareExchange(ref used, 1, 0) != 0)
                return;
            Entered.TrySetResult();
            await Release.Task.WaitAsync(cancellationToken);
        }
    }

    private sealed class ReadObserver : DbCommandInterceptor
    {
        public ConcurrentQueue<ObservedCommand> Commands { get; } = new();
        public ConcurrentQueue<string[]> Columns { get; } = new();
        public ReadGate? Gate { get; set; }

        public void Clear()
        {
            Commands.Clear();
            Columns.Clear();
        }

        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            Commands.Enqueue(new ObservedCommand(command.CommandText,
                command.Parameters.Cast<DbParameter>().Select(parameter => parameter.Value!).ToArray()));
            if (Gate is { } gate)
                await gate.BeforeAsync(command.CommandText, cancellationToken);
            return result;
        }

        public override ValueTask<DbDataReader> ReaderExecutedAsync(
            DbCommand command, CommandExecutedEventData eventData, DbDataReader result,
            CancellationToken cancellationToken = default)
        {
            Columns.Enqueue(Enumerable.Range(0, result.FieldCount).Select(result.GetName).ToArray());
            return ValueTask.FromResult(result);
        }
    }
}
