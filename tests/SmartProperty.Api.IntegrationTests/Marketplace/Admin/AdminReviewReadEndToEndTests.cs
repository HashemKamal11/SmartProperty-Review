using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SmartProperty.Api.IntegrationTests.Infrastructure;
using SmartProperty.Application.Authorization;
using SmartProperty.Domain.Marketplace;
using SmartProperty.Domain.PropertyRegistry;
using Xunit;

namespace SmartProperty.Api.IntegrationTests.Marketplace.Admin;

/// <summary>
/// The Platform Admin review reads end to end: real HTTP pipeline, the production permission checker, the real
/// PostgreSQL repository and real rows. Nothing is substituted.
/// </summary>
/// <remarks>
/// Runs against the shared disposable PostgreSQL fixture, so every assertion is about the rows this class seeds
/// (found by id) rather than about totals other tests may influence.
/// </remarks>
[Collection(ApiPostgreSqlCollection.Name)]
public sealed class AdminReviewReadEndToEndTests : IDisposable
{
    private const string Route = "/api/admin/marketplace/listing-submissions";
    private const string Sentinel = "SENTINEL-REASON passport 123456789";
    private static readonly DateTimeOffset T0 = new(2026, 11, 2, 12, 0, 0, TimeSpan.Zero);

    private readonly ApiPostgreSqlFixture _fixture;
    private readonly SmartPropertyApiFactory _factory;
    private readonly AuthScenario _scenario;

    public AdminReviewReadEndToEndTests(ApiPostgreSqlFixture fixture)
    {
        _fixture = fixture;
        _factory = new SmartPropertyApiFactory(fixture, usePersistencePermissionChecker: true);
        _scenario = new AuthScenario(fixture);
    }

    public void Dispose() => _factory.Dispose();

    public enum State { Draft, Pending, Approved, Rejected }

    // ----- who may read

    [Fact]
    public async Task WithoutATokenTheQueueIs401()
    {
        using var client = _factory.CreateClient();

        using var response = await client.GetAsync(Route);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task AnActiveUserWithNoGrantIs403OnBothReads()
    {
        var user = await CreateActiveUserAsync();
        var submission = await SeedAsync(State.Pending);
        using var client = Authenticated(user);

        using var list = await client.GetAsync(Route);
        using var detail = await client.GetAsync($"{Route}/{submission.Id}");

        Assert.Equal(HttpStatusCode.Forbidden, list.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, detail.StatusCode);
    }

    [Fact]
    public async Task HoldingOnlyTheDecidePermissionIs403OnBothReads()
    {
        var user = await CreateActiveUserAsync();
        await _scenario.GrantPlatformPermissionAsync(user, PermissionCodes.MarketplaceSubmissionsReviewDecide);
        var submission = await SeedAsync(State.Pending);
        using var client = Authenticated(user);

        using var list = await client.GetAsync(Route);
        using var detail = await client.GetAsync($"{Route}/{submission.Id}");

        Assert.Equal(HttpStatusCode.Forbidden, list.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, detail.StatusCode);
    }

    [Fact]
    public async Task TheReadPermissionHeldOnlyInsideAWorkspaceIs403()
    {
        var user = await CreateActiveUserAsync();
        var workspaceId = await _scenario.CreateWorkspaceAsync();
        await _scenario.GrantWorkspacePermissionAsync(user, workspaceId, PermissionCodes.MarketplaceSubmissionsReviewRead);
        using var client = Authenticated(user);

        using var list = await client.GetAsync(Route);

        Assert.Equal(HttpStatusCode.Forbidden, list.StatusCode);
    }

    [Fact]
    public async Task ThePlatformReadPermissionAloneIsEnoughAndNeedsNoReviewerIndependence()
    {
        // The reader is the very user who acted on the submission: independence would forbid deciding, not reading.
        var user = await CreateActiveUserAsync();
        await _scenario.GrantPlatformPermissionAsync(user, PermissionCodes.MarketplaceSubmissionsReviewRead);
        var own = await SeedAsync(State.Pending, actingUserId: user);
        using var client = Authenticated(user);

        using var response = await client.GetAsync($"{Route}/{own.Id}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Theory]
    [InlineData(SmartProperty.Domain.Identity.UserStatus.Suspended)]
    [InlineData(SmartProperty.Domain.Identity.UserStatus.Deactivated)]
    public async Task ASuspendedOrDeactivatedHolderOfThePlatformReadPermissionIs403OnBothReads(SmartProperty.Domain.Identity.UserStatus status)
    {
        var user = await CreateActiveUserAsync();
        await _scenario.GrantPlatformPermissionAsync(user, PermissionCodes.MarketplaceSubmissionsReviewRead);
        var submission = await SeedAsync(State.Pending);
        using var client = Authenticated(user);

        // Control: while active, the same user with the same grant and the same token reads.
        using (var before = await client.GetAsync($"{Route}/{submission.Id}"))
        {
            Assert.Equal(HttpStatusCode.OK, before.StatusCode);
        }

        await _scenario.UpdateUserAsync(
            user,
            status == SmartProperty.Domain.Identity.UserStatus.Suspended
                ? (candidate, now) => candidate.Suspend(now)
                : (candidate, now) => candidate.Deactivate(now));

        using var list = await client.GetAsync(Route);
        using var detail = await client.GetAsync($"{Route}/{submission.Id}");

        Assert.Equal(HttpStatusCode.Forbidden, list.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, detail.StatusCode);
    }

    // ----- malformed stored data fails closed

    [Fact]
    public async Task AMalformedAuthorityValueIsAGeneric500OnBothReadsThatDisclosesNothing()
    {
        using var client = await ReaderAsync();
        var submission = await SeedAsync(State.Pending);
        await using var context = _fixture.CreateContext();
        var original = await context.Database
            .SqlQueryRaw<string>("SELECT authority_snapshot::text AS \"Value\" FROM marketplace.submissions WHERE id = {0}", submission.Id)
            .SingleAsync();

        Assert.Equal(1, await context.Database.ExecuteSqlRawAsync(
            "UPDATE marketplace.submissions SET authority_snapshot = jsonb_set(authority_snapshot, ARRAY['Payload','ValidUntil'], to_jsonb('SENTINEL-NOT-A-DATE'::text)) WHERE id = {0}",
            submission.Id));

        try
        {
            using var detail = await client.GetAsync($"{Route}/{submission.Id}");
            using var list = await client.GetAsync(Route);

            await AssertGenericServerErrorAsync(detail);
            await AssertGenericServerErrorAsync(list);
        }
        finally
        {
            // The fixture database is shared by this collection: restore the row so no later queue read meets it.
            await context.Database.ExecuteSqlRawAsync(
                "UPDATE marketplace.submissions SET authority_snapshot = {0}::jsonb WHERE id = {1}", original, submission.Id);
        }
    }

    // ----- what is read

    [Fact]
    public async Task TheQueueListsPendingSubmissionsOldestFirstExcludesDraftsAndFiltersByPurpose()
    {
        using var client = await ReaderAsync();
        var sale = await SeedAsync(State.Pending, ListingPurpose.Sale, minute: 1);
        var rent = await SeedAsync(State.Pending, ListingPurpose.Rent, minute: 2);
        var draft = await SeedAsync(State.Draft, minute: 3);
        var rejected = await SeedAsync(State.Rejected, minute: 4);

        var all = await ListIdsAsync(client, "?pageSize=100");
        var rentOnly = await ListIdsAsync(client, "?purpose=Rent&pageSize=100");

        Assert.Contains(sale.Id, all);
        Assert.Contains(rent.Id, all);
        Assert.DoesNotContain(draft.Id, all);
        Assert.DoesNotContain(rejected.Id, all);
        Assert.True(all.IndexOf(sale.Id) < all.IndexOf(rent.Id));
        Assert.Contains(rent.Id, rentOnly);
        Assert.DoesNotContain(sale.Id, rentOnly);
    }

    [Fact]
    public async Task ADraftStatusFilterIsRefusedAndADraftHasNoDetail()
    {
        using var client = await ReaderAsync();
        var draft = await SeedAsync(State.Draft);

        using var list = await client.GetAsync($"{Route}?status=Draft");
        using var detail = await client.GetAsync($"{Route}/{draft.Id}");

        Assert.Contains((int)list.StatusCode, new[] { 400, 422 });
        Assert.Equal(HttpStatusCode.NotFound, detail.StatusCode);
    }

    [Fact]
    public async Task PagingSplitsTheQueueWithoutOverlap()
    {
        using var client = await ReaderAsync();
        await SeedAsync(State.Pending, minute: 1);
        await SeedAsync(State.Pending, minute: 2);

        using var first = await client.GetAsync($"{Route}?pageSize=1&page=1");
        using var second = await client.GetAsync($"{Route}?pageSize=1&page=2");
        using var firstBody = JsonDocument.Parse(await first.Content.ReadAsStringAsync());
        using var secondBody = JsonDocument.Parse(await second.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.Equal(1, firstBody.RootElement.GetProperty("items").GetArrayLength());
        Assert.Equal(1, secondBody.RootElement.GetProperty("items").GetArrayLength());
        Assert.True(firstBody.RootElement.GetProperty("totalCount").GetInt64() >= 2);
        Assert.True(firstBody.RootElement.GetProperty("hasNext").GetBoolean());
        Assert.NotEqual(
            firstBody.RootElement.GetProperty("items")[0].GetProperty("submissionId").GetGuid(),
            secondBody.RootElement.GetProperty("items")[0].GetProperty("submissionId").GetGuid());
    }

    [Fact]
    public async Task ARejectedSubmissionIsReadWithItsOutcomeButNeverItsReasonOrAnySensitiveValue()
    {
        using var client = await ReaderAsync();
        var rejected = await SeedAsync(State.Rejected, reason: Sentinel);

        using var detail = await client.GetAsync($"{Route}/{rejected.Id}");
        var detailBody = await detail.Content.ReadAsStringAsync();
        using var list = await client.GetAsync($"{Route}?status=Rejected&pageSize=100");
        var listBody = await list.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, detail.StatusCode);
        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        using var document = JsonDocument.Parse(detailBody);
        var decision = document.RootElement.GetProperty("decision");
        Assert.False(decision.GetProperty("approved").GetBoolean());
        Assert.False(decision.TryGetProperty("reason", out _));
        Assert.Contains(rejected.Id.ToString(), listBody, StringComparison.OrdinalIgnoreCase);

        foreach (var body in new[] { detailBody, listBody })
        {
            AssertNothingRestricted(body, rejected);
        }
    }

    [Fact]
    public async Task AnApprovedSubmissionIsReadAsCurrentlyPublishedWithoutAnySensitiveValue()
    {
        using var client = await ReaderAsync();
        var approved = await SeedAsync(State.Approved);

        using var detail = await client.GetAsync($"{Route}/{approved.Id}");
        var body = await detail.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, detail.StatusCode);
        using var document = JsonDocument.Parse(body);
        Assert.True(document.RootElement.GetProperty("decision").GetProperty("approved").GetBoolean());
        Assert.True(document.RootElement.GetProperty("allEvidenceClean").GetBoolean());
        Assert.True(document.RootElement.GetProperty("isCurrentlyPublished").GetBoolean());
        AssertNothingRestricted(body, approved);
    }

    [Fact]
    public async Task AMissingSubmissionIs404()
    {
        using var client = await ReaderAsync();

        using var response = await client.GetAsync($"{Route}/{Guid.NewGuid()}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Theory]
    [InlineData("approve")]
    [InlineData("reject")]
    public async Task ApproveAndRejectStayUnroutableEvenWithBothPermissionsHeld(string action)
    {
        var user = await CreateActiveUserAsync();
        await _scenario.GrantPlatformPermissionAsync(user, PermissionCodes.MarketplaceSubmissionsReviewRead);
        await _scenario.GrantPlatformPermissionAsync(user, PermissionCodes.MarketplaceSubmissionsReviewDecide);
        var pending = await SeedAsync(State.Pending);
        using var client = Authenticated(user);

        using var response = await client.PostAsync(
            $"{Route}/{pending.Id}/{action}",
            new StringContent("""{"expectedVersion":1,"reason":"x"}""", Encoding.UTF8, new MediaTypeHeaderValue("application/json")));
        using var after = await client.GetAsync($"{Route}/{pending.Id}");
        using var afterBody = JsonDocument.Parse(await after.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("PendingReview", afterBody.RootElement.GetProperty("status").GetString());
    }

    // ----- helpers

    private static async Task AssertGenericServerErrorAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        using var document = JsonDocument.Parse(body);
        Assert.Equal("server.unexpected_error", document.RootElement.GetProperty("code").GetString());

        foreach (var leaked in new[] { "SENTINEL", "Postgres", "Npgsql", "22007", "timestamp", "authority_snapshot", "SELECT" })
        {
            Assert.DoesNotContain(leaked, body, StringComparison.OrdinalIgnoreCase);
        }
    }

    private static void AssertNothingRestricted(string body, ListingSubmission submission)
    {
        var restricted = new[]
        {
            Sentinel, "SENTINEL", "123456789", new string('A', 64),
            submission.Authority.ActingUserId.ToString(),
            submission.Authority.DeclaredOwner.LegalPartyReferenceId.ToString(),
            submission.Evidence[0].EvidenceId.ToString(),
            submission.Evidence[0].VersionId.ToString(),
        };

        foreach (var value in restricted)
        {
            Assert.DoesNotContain(value, body, StringComparison.OrdinalIgnoreCase);
        }

        foreach (var name in new[] { "reason", "sha256", "digest", "legalParty", "linkedUser", "actingUser", "snapshot", "assessment", "workspace" })
        {
            Assert.DoesNotContain($"\"{name}", body, StringComparison.OrdinalIgnoreCase);
        }
    }

    private static async Task<List<Guid>> ListIdsAsync(HttpClient client, string query)
    {
        using var response = await client.GetAsync(Route + query);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        return document.RootElement.GetProperty("items").EnumerateArray()
            .Select(item => item.GetProperty("submissionId").GetGuid())
            .ToList();
    }

    private HttpClient Authenticated(Guid userId) =>
        AuthScenario.Authenticate(_factory.CreateClient(), TestJwt.CreateAccessToken(userId));

    private async Task<Guid> CreateActiveUserAsync()
    {
        using var client = _factory.CreateClient();
        var user = await _scenario.RegisterActiveUserAsync(client);

        return user.UserId;
    }

    private async Task<HttpClient> ReaderAsync()
    {
        var user = await CreateActiveUserAsync();
        await _scenario.GrantPlatformPermissionAsync(user, PermissionCodes.MarketplaceSubmissionsReviewRead);

        return Authenticated(user);
    }

    private async Task<ListingSubmission> SeedAsync(
        State state,
        ListingPurpose purpose = ListingPurpose.Sale,
        int minute = 1,
        string reason = "Not enough evidence.",
        Guid? actingUserId = null)
    {
        await using var context = _fixture.CreateContext();
        var property = new Property(Guid.NewGuid(), PropertyType.Unit, new PropertyAddress("EG"), T0);
        var listing = new PropertyListing(Guid.NewGuid(), property.Id, purpose, T0);
        context.Add(property);
        context.Add(listing);
        await context.SaveChangesAsync();

        var at = T0.AddMinutes(minute);
        var actor = actingUserId ?? Guid.NewGuid();
        var authority = new ListingLegalAuthority(listing.PropertyId, listing.Purpose,
            new ListingOwnerIdentity(ListingOwnerKind.Individual, Guid.NewGuid(), actor), actor,
            ListingRepresentationKind.Owner, null, listing.CreatedAt, null);
        var evidence = new ListingEvidenceReference(Guid.NewGuid(), Guid.NewGuid(), listing.PropertyId, listing.Purpose, new string('A', 64));
        var content = new ListingSubmissionContent("Country", "Region", "City", "District", "Title", "Description",
            PropertyType.Unit, listing.Purpose, 100m, "USD", listing.Purpose == ListingPurpose.Rent ? RentPeriod.Monthly : null);
        var submission = new ListingSubmission(Guid.NewGuid(), listing, content, authority, [evidence], at, null);

        if (state != State.Draft)
        {
            submission.Submit(actor, null, at, Assessments(submission, at), true, null);
        }

        context.Add(submission);
        await context.SaveChangesAsync();

        var decidedAt = at.AddSeconds(30);

        if (state == State.Approved)
        {
            submission.ApproveAndPublish(Guid.NewGuid(), listing, null, Reviewer(submission), decidedAt, true,
                Assessments(submission, decidedAt), true, null);
            await context.SaveChangesAsync();
        }
        else if (state == State.Rejected)
        {
            submission.Reject(Guid.NewGuid(), Reviewer(submission), reason, decidedAt);
            await context.SaveChangesAsync();
        }

        return submission;
    }

    private static ListingReviewContext Reviewer(ListingSubmission submission) => new(Guid.NewGuid(),
        submission.Authority.ActingUserId, submission.Authority.DeclaredOwner, submission.Authority.WorkspaceId,
        true, true, submission.Authority.WorkspaceId.HasValue ? false : null, false);

    private static ListingEvidenceAssessment[] Assessments(ListingSubmission submission, DateTimeOffset at) =>
        submission.Evidence.Select(e => new ListingEvidenceAssessment(Guid.NewGuid(), e, EvidenceScanStatus.Clean, at)).ToArray();
}
