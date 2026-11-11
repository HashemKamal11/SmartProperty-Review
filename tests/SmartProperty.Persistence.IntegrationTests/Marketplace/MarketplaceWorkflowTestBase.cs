using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using SmartProperty.Application.Abstractions.Authorization;
using SmartProperty.Application.Abstractions.Identity;
using SmartProperty.Application.Abstractions.Marketplace;
using SmartProperty.Application.Abstractions.Persistence.Repositories;
using SmartProperty.Application.Abstractions.Time;
using SmartProperty.Application.Marketplace.ListingSubmissions.Review.Decide;
using SmartProperty.Application.Marketplace.ListingSubmissions.Write;
using SmartProperty.Application.Marketplace.ListingSubmissions.Write.Create;
using SmartProperty.Application.Marketplace.ListingSubmissions.Write.CreateBoth;
using SmartProperty.Application.Marketplace.ListingSubmissions.Write.Submit;
using SmartProperty.Application.Marketplace.ListingSubmissions.Write.Withdraw;
using SmartProperty.Common.Results;
using SmartProperty.Domain.Identity;
using SmartProperty.Domain.Marketplace;
using SmartProperty.Domain.PropertyRegistry;
using SmartProperty.Persistence.IntegrationTests.Infrastructure;
using Xunit;

namespace SmartProperty.Persistence.IntegrationTests.Marketplace;

/// <summary>
/// Shared set-up for the workflow tests that run the real handlers against real PostgreSQL: a seeded owner,
/// reviewer and stranger, explicitly seeded grants (nothing issues them), and test fakes for the evidence provider, the
/// reviewer-independence provider and the decide authorizer, which production registers as ones that never answer.
/// </summary>
public abstract class MarketplaceWorkflowTestBase(PostgreSqlFixture fixture) : DatabaseTest(fixture)
{
    /// <summary>The clock every handler in a test uses; a test moves it forward to make time pass.</summary>
    protected AdjustableClock Clock { get; } = new();

    protected sealed record World(Guid PropertyId, Guid Owner, Guid Issuer, Guid LegalReviewer, Guid Stranger, Guid Reviewer);

    protected static ListingDraftContentInput SaleContent(Guid[]? evidenceIds = null) =>
        new("Villa for sale", "A villa", 5_000_000m, "EGP", null, evidenceIds ?? []);

    protected static ListingDraftContentInput RentContent(Guid[]? evidenceIds = null) =>
        new("Villa for rent", "A villa", 0m, string.Empty, null, evidenceIds ?? [],
            [new RentPricingOptionInput(RentPeriod.Monthly, 20_000m, "EGP"), new RentPricingOptionInput(RentPeriod.Daily, 1_000m, "EGP")],
            new RentalTermsInput(new TimeOnly(14, 0), new TimeOnly(11, 0),
                [new RentalWindowInput(new DateOnly(2027, 6, 1), new DateOnly(2027, 9, 1))]));

    protected ScopeHandle Scope() => new(Host.CreateScope());

    protected async Task<World> SeedAsync(ListingPurpose[] grants)
    {
        var at = DateTimeOffset.UtcNow.AddDays(-30);
        var users = Enumerable.Range(0, 5)
            .Select(_ => new User(Guid.NewGuid(), $"{Guid.NewGuid():n}@example.com", "Test", "User", at))
            .ToArray();
        foreach (var user in users)
        {
            user.Activate(at);
        }

        var aiId = Random.Shared.Next(10_000_000, 2_000_000_000);
        var country = new SmartProperty.Domain.Locations.Country(Guid.NewGuid(), aiId, $"Country {aiId}", "بلد", "EG");
        var city = new SmartProperty.Domain.Locations.City(Guid.NewGuid(), aiId, country, "Cairo", "القاهرة");
        var area = new SmartProperty.Domain.Locations.Area(Guid.NewGuid(), aiId, city, "Maadi", "المعادي");
        var property = new Property(
            Guid.NewGuid(),
            PropertyType.Villa,
            new PropertyAddress("EG", city: "Cairo", region: "Cairo Governorate", district: "Maadi"),
            at,
            area: area);

        await using (var context = Host.CreateVerificationContext())
        {
            context.Users.AddRange(users);
            context.Areas.Add(area);
            context.Properties.Add(property);
            await context.SaveChangesAsync();
        }

        var world = new World(property.Id, users[0].Id, users[1].Id, users[2].Id, users[3].Id, users[4].Id);

        foreach (var purpose in grants)
        {
            await SeedGrantAsync(world, purpose);
        }

        return world;
    }

    protected async Task SeedGrantAsync(World world, ListingPurpose purpose)
    {
        var now = DateTimeOffset.UtcNow;
        var owner = new ListingOwnerIdentity(ListingOwnerKind.Individual, Guid.NewGuid(), world.Owner);
        var authority = new ListingLegalAuthority(
            world.PropertyId, purpose, owner, world.Owner, ListingRepresentationKind.Owner, null, now.AddDays(-20), now.AddDays(300));
        ListingReviewContext Reviewer(Guid id) => new(id, world.Owner, owner, null, true, true, null, false);
        var grant = PropertyAuthorityGrant.Issue(
            Guid.NewGuid(), authority, Reviewer(world.Issuer), Guid.NewGuid(), Reviewer(world.LegalReviewer),
            now.AddDays(-6), now.AddDays(-5), legalAuthorityVerified: true);

        await using var context = Host.CreateVerificationContext();
        context.PropertyAuthorityGrants.Add(grant);
        await context.SaveChangesAsync();
    }

    protected async Task RevokeAllGrantsAsync(World world)
    {
        await using var context = Host.CreateVerificationContext();

        foreach (var grant in context.PropertyAuthorityGrants.Where(g => g.PropertyId == world.PropertyId).ToList())
        {
            var reviewer = new ListingReviewContext(world.Stranger, grant.GranteeUserId, grant.Authority.DeclaredOwner, null, true, true, null, false);
            grant.Revoke(grant.Version, reviewer, DateTimeOffset.UtcNow.AddSeconds(-1));
        }

        await context.SaveChangesAsync();
    }

    // Creates a draft with one piece of evidence, then submits it. Each call to this helper is the next version.
    protected async Task<ListingDraftResult> SubmittedAsync(World world, ListingPurpose purpose)
    {
        var content = purpose == ListingPurpose.Sale ? SaleContent([Guid.NewGuid()]) : RentContent([Guid.NewGuid()]);
        Result<ListingDraftResult> created;

        await using (var scope = Scope())
        {
            var handler = new CreateListingDraftCommandHandler(
                new FakeCurrentUser(world.Owner), scope.Get<IUserRepository>(), scope.Get<IListingSubmissionWriteRepository>(),
                new FakeEvidence(), Clock);
            created = await handler.Handle(new CreateListingDraftCommand(world.PropertyId, purpose, content));
        }

        Assert.True(created.IsSuccess, created.Error?.Code);

        await using (var scope = Scope())
        {
            var handler = new SubmitListingDraftCommandHandler(
                new FakeCurrentUser(world.Owner), scope.Get<IUserRepository>(), scope.Get<IListingSubmissionWriteRepository>(),
                new FakeEvidence(), Clock);
            var submitted = await handler.Handle(new SubmitListingDraftCommand(created.Value.SubmissionId, created.Value.Revision));
            Assert.True(submitted.IsSuccess, submitted.Error?.Code);
        }

        return created.Value;
    }

    protected async Task<Result<PropertyListingDraftsResult>> CreateBothAsync(
        World world, ListingDraftContentInput? sale, ListingDraftContentInput? rent)
    {
        await using var scope = Scope();
        var handler = new CreatePropertyListingDraftsCommandHandler(
            new FakeCurrentUser(world.Owner), scope.Get<IUserRepository>(), scope.Get<IListingSubmissionWriteRepository>(),
            new FakeEvidence(), Clock);

        return await handler.Handle(new CreatePropertyListingDraftsCommand(world.PropertyId, sale, rent));
    }

    protected async Task<Result<ReviewDecisionResult>> ApproveAsync(
        World world,
        Guid submissionId,
        Guid? reviewer = null,
        bool allowed = true,
        FakeEvidence? evidence = null,
        FakeIndependence? independence = null)
    {
        await using var scope = Scope();
        var handler = new ApproveListingSubmissionCommandHandler(
            new FakeCurrentUser(reviewer ?? world.Reviewer), scope.Get<IUserRepository>(), new FakeDecisionAuthorizer(allowed),
            independence ?? new FakeIndependence(new ReviewerIndependenceFacts(true, false, null)),
            scope.Get<IMarketplaceRequestWriteRepository>(), evidence ?? new FakeEvidence(), Clock);

        return await handler.Handle(new ApproveListingSubmissionCommand(submissionId));
    }

    protected async Task<Result<ReviewDecisionResult>> RejectAsync(
        World world, Guid submissionId, string reason, FakeIndependence? independence = null)
    {
        await using var scope = Scope();
        var handler = new RejectListingSubmissionCommandHandler(
            new FakeCurrentUser(world.Reviewer), scope.Get<IUserRepository>(), new FakeDecisionAuthorizer(true),
            independence ?? new FakeIndependence(new ReviewerIndependenceFacts(true, false, null)),
            scope.Get<IListingSubmissionWriteRepository>(), Clock);

        return await handler.Handle(new RejectListingSubmissionCommand(submissionId, reason));
    }

    protected async Task<Result<WithdrawListingResult>> WithdrawAsync(World world, Guid listingId, Guid? actor = null)
    {
        await using var scope = Scope();
        var handler = new WithdrawListingCommandHandler(
            new FakeCurrentUser(actor ?? world.Owner), scope.Get<IUserRepository>(), scope.Get<IListingSubmissionWriteRepository>(),
            Clock);

        return await handler.Handle(new WithdrawListingCommand(listingId));
    }

    protected async Task<int> CountAsync(string table) => (int)await ScalarAsync($"SELECT count(*) FROM {table}");

    protected async Task<long> ScalarAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(Host.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);

        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }

    protected async Task<object?> ScalarObjectAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(Host.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);

        return await command.ExecuteScalarAsync();
    }

    protected async Task<string> TextAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(Host.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);

        return (string)(await command.ExecuteScalarAsync())!;
    }

    protected async Task<Guid> GuidAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(Host.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);

        return (Guid)(await command.ExecuteScalarAsync())!;
    }

    protected sealed class ScopeHandle(IServiceScope scope) : IAsyncDisposable
    {
        public T Get<T>() where T : notnull => scope.ServiceProvider.GetRequiredService<T>();

        public ValueTask DisposeAsync()
        {
            if (scope is IAsyncDisposable asyncDisposable)
            {
                return asyncDisposable.DisposeAsync();
            }

            scope.Dispose();

            return ValueTask.CompletedTask;
        }
    }

    protected sealed class FakeCurrentUser(Guid userId) : ICurrentUser
    {
        public Guid? UserId => userId;

        public bool IsAuthenticated => true;
    }

    protected sealed class AdjustableClock : IDateTimeProvider
    {
        public DateTimeOffset? Fixed { get; set; }

        public DateTimeOffset UtcNow => Fixed ?? DateTimeOffset.UtcNow;
    }

    protected sealed class FakeDecisionAuthorizer(bool allowed) : IReviewDecisionAuthorizer
    {
        public Task<bool> IsAuthorizedAsync(Guid reviewerUserId, CancellationToken cancellationToken = default) =>
            Task.FromResult(allowed);
    }

    protected sealed class FakeIndependence(ReviewerIndependenceFacts facts) : IReviewerIndependenceProvider
    {
        public Task<ReviewerIndependenceFacts> GetAsync(
            Guid reviewerUserId, Guid submitterUserId, ListingOwnerIdentity declaredOwner, Guid? submittingWorkspaceId,
            CancellationToken cancellationToken = default) => Task.FromResult(facts);
    }

    /// <summary>Test-only provider: resolves and cleans whatever it is asked about while available.</summary>
    protected sealed class FakeEvidence : IEvidenceAssessmentProvider
    {
        public bool Available { get; set; } = true;

        public Task<EvidenceResolution> ResolveAsync(
            Guid actingUserId, Guid propertyId, ListingPurpose purpose, IReadOnlyCollection<Guid> evidenceIds,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Available
                ? EvidenceResolution.Resolved(evidenceIds
                    .Select(id => new ListingEvidenceReference(id, Guid.NewGuid(), propertyId, purpose, new string('A', 64)))
                    .ToArray())
                : EvidenceResolution.Unavailable());
        }

        public Task<EvidenceAssessmentResult> AssessAsync(
            IReadOnlyCollection<ListingEvidenceReference> evidence, DateTimeOffset at,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Available
                ? EvidenceAssessmentResult.Assessed(evidence
                    .Select(reference => new ListingEvidenceAssessment(Guid.NewGuid(), reference, EvidenceScanStatus.Clean, at))
                    .ToArray())
                : EvidenceAssessmentResult.Unavailable());
        }
    }
}
