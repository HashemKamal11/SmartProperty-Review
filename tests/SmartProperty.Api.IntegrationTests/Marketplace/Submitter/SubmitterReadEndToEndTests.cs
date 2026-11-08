using System.Net;
using System.Text.Json;
using SmartProperty.Api.IntegrationTests.Infrastructure;
using SmartProperty.Domain.Marketplace;
using SmartProperty.Domain.PropertyRegistry;
using Xunit;

namespace SmartProperty.Api.IntegrationTests.Marketplace.Submitter;

/// <summary>
/// The submitter reads end to end: real HTTP pipeline, the real account lookup, the real PostgreSQL repository and real
/// rows. Nothing is substituted.
/// </summary>
/// <remarks>
/// Runs against the shared disposable PostgreSQL fixture. Every test creates its own users, so a list is exactly the
/// rows that test seeded for that user, whatever other tests leave behind. Ownership is the acting user recorded in the
/// submission's legal-authority declaration, never the declared owner's linked user.
/// </remarks>
[Collection(ApiPostgreSqlCollection.Name)]
public sealed class SubmitterReadEndToEndTests : IDisposable
{
    private const string Route = "/api/marketplace/me/listing-submissions";
    private const string PublicRoute = "/api/public/marketplace/listings";
    private const string AdminRoute = "/api/admin/marketplace/listing-submissions";
    private const string RejectionReason = "SENTINEL-REASON not enough evidence";
    private static readonly DateTimeOffset T0 = new(2026, 11, 2, 12, 0, 0, TimeSpan.Zero);

    private static readonly string[] ForbiddenTerms =
    [
        "propertyId", "legalParty", "linkedUser", "actingUser", "userId", "submitterId", "workspace", "evidence", "sha256",
        "digest", "assessment", "snapshot", "reviewer", "ownerKind", "grant", "verification",
    ];

    private readonly ApiPostgreSqlFixture _fixture;
    private readonly SmartPropertyApiFactory _factory;
    private readonly AuthScenario _scenario;

    public SubmitterReadEndToEndTests(ApiPostgreSqlFixture fixture)
    {
        _fixture = fixture;
        _factory = new SmartPropertyApiFactory(fixture, usePersistencePermissionChecker: true);
        _scenario = new AuthScenario(fixture);
    }

    public void Dispose() => _factory.Dispose();

    public enum State { Draft, Pending, Approved, Rejected }

    // ----- who may read

    [Fact]
    public async Task WithoutATokenBothReadsAre401()
    {
        using var client = _factory.CreateClient();

        using var list = await client.GetAsync(Route);
        using var detail = await client.GetAsync($"{Route}/{Guid.NewGuid()}");

        Assert.Equal(HttpStatusCode.Unauthorized, list.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, detail.StatusCode);
    }

    [Fact]
    public async Task APendingAccountIs403AccountUnavailableOnBothReads()
    {
        using var registration = _factory.CreateClient();
        var user = (await _scenario.RegisterAsync(registration, await _scenario.CreateWorkspaceAsync())).UserId;
        var own = await SeedAsync(user, State.Pending);
        using var client = Authenticated(user);

        await AssertAccountUnavailableAsync(await client.GetAsync(Route));
        await AssertAccountUnavailableAsync(await client.GetAsync($"{Route}/{own.Id}"));
    }

    [Fact]
    public async Task ASuspendedOrDeactivatedAccountIs403AccountUnavailableOnBothReads()
    {
        var suspended = await CreateActiveUserAsync();
        var deactivated = await CreateActiveUserAsync();
        var suspendedOwn = await SeedAsync(suspended, State.Pending);
        var deactivatedOwn = await SeedAsync(deactivated, State.Pending);
        await _scenario.UpdateUserAsync(suspended, (user, now) => user.Suspend(now));
        await _scenario.UpdateUserAsync(deactivated, (user, now) => user.Deactivate(now));

        foreach (var (user, own) in new[] { (suspended, suspendedOwn), (deactivated, deactivatedOwn) })
        {
            using var client = Authenticated(user);

            await AssertAccountUnavailableAsync(await client.GetAsync(Route));
            await AssertAccountUnavailableAsync(await client.GetAsync($"{Route}/{own.Id}"));
        }
    }

    // ----- whose submissions

    [Fact]
    public async Task EachUserSeesExactlyTheirOwnSubmissions()
    {
        var mine = await CreateActiveUserAsync();
        var theirs = await CreateActiveUserAsync();
        var a = await SeedAsync(mine, State.Pending, minute: 1);
        var b = await SeedAsync(mine, State.Draft, minute: 2);
        var other = await SeedAsync(theirs, State.Pending, minute: 3);
        using var client = Authenticated(mine);

        using var response = await client.GetAsync(Route);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var ids = Ids(document);
        Assert.Equal(new[] { a.Id, b.Id }.Order().ToArray(), ids.Order().ToArray());
        Assert.DoesNotContain(other.Id, ids);
        Assert.Equal(2, document.RootElement.GetProperty("totalCount").GetInt64());
    }

    [Fact]
    public async Task AnotherUsersSubmissionAndAMissingOneAreTheSame404()
    {
        var mine = await CreateActiveUserAsync();
        var theirs = await CreateActiveUserAsync();
        var other = await SeedAsync(theirs, State.Pending);
        using var client = Authenticated(mine);

        using var foreign = await client.GetAsync($"{Route}/{other.Id}");
        using var missing = await client.GetAsync($"{Route}/{Guid.NewGuid()}");

        Assert.Equal(HttpStatusCode.NotFound, foreign.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        Assert.Equal(await WithoutCorrelationAsync(missing), await WithoutCorrelationAsync(foreign));
        using var document = JsonDocument.Parse(await foreign.Content.ReadAsStringAsync());
        Assert.Equal("listing_submissions.not_found", document.RootElement.GetProperty("code").GetString());
    }

    [Fact]
    public async Task OwnershipIsTheActingUserNotTheDeclaredOwnersLinkedUser()
    {
        var linkedOwner = await CreateActiveUserAsync();
        var actor = await CreateActiveUserAsync();
        var submission = await SeedAsync(actor, State.Pending, linkedUserId: linkedOwner);
        using var ownerClient = Authenticated(linkedOwner);
        using var actorClient = Authenticated(actor);

        using var ownerList = await ownerClient.GetAsync(Route);
        using var ownerDetail = await ownerClient.GetAsync($"{Route}/{submission.Id}");
        using var actorDetail = await actorClient.GetAsync($"{Route}/{submission.Id}");

        using var ownerDocument = JsonDocument.Parse(await ownerList.Content.ReadAsStringAsync());
        Assert.Empty(Ids(ownerDocument));
        Assert.Equal(HttpStatusCode.NotFound, ownerDetail.StatusCode);
        Assert.Equal(HttpStatusCode.OK, actorDetail.StatusCode);
    }

    // ----- what is shown

    [Fact]
    public async Task TheOwnersDraftIsShownWithoutASubmissionTimeOrADecision()
    {
        var user = await CreateActiveUserAsync();
        var draft = await SeedAsync(user, State.Draft);
        using var client = Authenticated(user);

        using var response = await client.GetAsync($"{Route}/{draft.Id}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = document.RootElement;
        Assert.Equal("Draft", root.GetProperty("status").GetString());
        Assert.False(root.TryGetProperty("submittedAt", out _));
        Assert.False(root.TryGetProperty("decision", out _));
        Assert.False(root.GetProperty("isCurrentlyPublished").GetBoolean());
    }

    [Fact]
    public async Task ARejectionShowsItsReasonAndAnApprovalShowsNone()
    {
        var user = await CreateActiveUserAsync();
        var rejected = await SeedAsync(user, State.Rejected, minute: 1);
        var approved = await SeedAsync(user, State.Approved, minute: 2);
        using var client = Authenticated(user);

        using var rejectedResponse = await client.GetAsync($"{Route}/{rejected.Id}");
        using var approvedResponse = await client.GetAsync($"{Route}/{approved.Id}");

        Assert.Equal(HttpStatusCode.OK, rejectedResponse.StatusCode);
        Assert.Equal(HttpStatusCode.OK, approvedResponse.StatusCode);
        using var rejectedDocument = JsonDocument.Parse(await rejectedResponse.Content.ReadAsStringAsync());
        using var approvedDocument = JsonDocument.Parse(await approvedResponse.Content.ReadAsStringAsync());
        var rejectedDecision = rejectedDocument.RootElement.GetProperty("decision");
        var approvedDecision = approvedDocument.RootElement.GetProperty("decision");
        Assert.False(rejectedDecision.GetProperty("approved").GetBoolean());
        Assert.Equal(RejectionReason, rejectedDecision.GetProperty("reason").GetString());
        Assert.True(approvedDecision.GetProperty("approved").GetBoolean());
        Assert.False(approvedDecision.TryGetProperty("reason", out _));
        Assert.True(approvedDocument.RootElement.GetProperty("isCurrentlyPublished").GetBoolean());
    }

    [Fact]
    public async Task NoResponseCarriesAPrivatePropertyName()
    {
        var user = await CreateActiveUserAsync();
        var rejected = await SeedAsync(user, State.Rejected, purpose: ListingPurpose.Rent);
        await SeedAsync(user, State.Approved);
        await SeedAsync(user, State.Draft);
        using var client = Authenticated(user);

        using var list = await client.GetAsync(Route);
        using var detail = await client.GetAsync($"{Route}/{rejected.Id}");

        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        Assert.Equal(HttpStatusCode.OK, detail.StatusCode);
        AssertNoForbiddenTerms(await list.Content.ReadAsStringAsync());
        AssertNoForbiddenTerms(await detail.Content.ReadAsStringAsync());
    }

    // ----- order, filters and paging

    [Fact]
    public async Task TheListIsUpdatedAtDescendingThenSubmissionIdAscending()
    {
        var user = await CreateActiveUserAsync();
        var oldest = await SeedAsync(user, State.Pending, minute: 1);
        var tiedA = await SeedAsync(user, State.Pending, minute: 2);
        var tiedB = await SeedAsync(user, State.Pending, minute: 2);
        var newest = await SeedAsync(user, State.Pending, minute: 3);
        using var client = Authenticated(user);

        using var response = await client.GetAsync(Route);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        // PostgreSQL orders a uuid byte by byte: the ordinal order of the canonical text.
        var tied = new[] { tiedA.Id, tiedB.Id }.OrderBy(id => id.ToString("D"), StringComparer.Ordinal).ToArray();
        Assert.Equal(new[] { newest.Id, tied[0], tied[1], oldest.Id }, Ids(document));
    }

    [Fact]
    public async Task PurposeAndStatusFiltersAndPagingAreAppliedByTheDatabase()
    {
        var user = await CreateActiveUserAsync();
        var saleDraft = await SeedAsync(user, State.Draft, minute: 1);
        await SeedAsync(user, State.Pending, minute: 2);
        var rentPending = await SeedAsync(user, State.Pending, purpose: ListingPurpose.Rent, minute: 3);
        var rentDraft = await SeedAsync(user, State.Draft, purpose: ListingPurpose.Rent, minute: 4);
        using var client = Authenticated(user);

        using var rent = await client.GetAsync($"{Route}?purpose=Rent");
        using var drafts = await client.GetAsync($"{Route}?status=Draft");
        using var rentDrafts = await client.GetAsync($"{Route}?purpose=Rent&status=Draft");
        using var secondPage = await client.GetAsync($"{Route}?page=2&pageSize=1");

        using var rentDocument = JsonDocument.Parse(await rent.Content.ReadAsStringAsync());
        using var draftsDocument = JsonDocument.Parse(await drafts.Content.ReadAsStringAsync());
        using var rentDraftsDocument = JsonDocument.Parse(await rentDrafts.Content.ReadAsStringAsync());
        using var pageDocument = JsonDocument.Parse(await secondPage.Content.ReadAsStringAsync());
        Assert.Equal(new[] { rentDraft.Id, rentPending.Id }, Ids(rentDocument));
        Assert.Equal(new[] { rentDraft.Id, saleDraft.Id }, Ids(draftsDocument));
        Assert.Equal(new[] { rentDraft.Id }, Ids(rentDraftsDocument));

        var page = pageDocument.RootElement;
        Assert.Equal(new[] { rentPending.Id }, Ids(pageDocument));
        Assert.Equal(2, page.GetProperty("page").GetInt32());
        Assert.Equal(1, page.GetProperty("pageSize").GetInt32());
        Assert.Equal(4, page.GetProperty("totalCount").GetInt64());
        Assert.True(page.GetProperty("hasNext").GetBoolean());
        Assert.True(page.GetProperty("hasPrevious").GetBoolean());
    }

    // ----- other surfaces

    [Fact]
    public async Task TheOwnersDraftIsNeitherPublicNorInTheDefaultReviewQueue()
    {
        var user = await CreateActiveUserAsync();
        var draft = await SeedAsync(user, State.Draft);
        var reviewer = await CreateActiveUserAsync();
        await _scenario.GrantPlatformPermissionAsync(reviewer, SmartProperty.Application.Authorization.PermissionCodes.MarketplaceSubmissionsReviewRead);
        using var anonymous = _factory.CreateClient();
        using var reviewerClient = Authenticated(reviewer);

        using var publicDetail = await anonymous.GetAsync($"{PublicRoute}/{draft.ListingId}");
        using var queue = await reviewerClient.GetAsync($"{AdminRoute}?pageSize=100");

        Assert.Equal(HttpStatusCode.NotFound, publicDetail.StatusCode);
        Assert.Equal(HttpStatusCode.OK, queue.StatusCode);
        using var queueDocument = JsonDocument.Parse(await queue.Content.ReadAsStringAsync());
        Assert.DoesNotContain(draft.Id, Ids(queueDocument));
    }

    // ----- helpers

    private HttpClient Authenticated(Guid userId) =>
        AuthScenario.Authenticate(_factory.CreateClient(), TestJwt.CreateAccessToken(userId));

    private async Task<Guid> CreateActiveUserAsync()
    {
        using var client = _factory.CreateClient();
        var user = await _scenario.RegisterActiveUserAsync(client);

        return user.UserId;
    }

    // Seeds through the Domain, as the Admin review end-to-end tests do: the actor recorded in the legal-authority
    // declaration is the given user, and the declared owner's linked user is the actor unless another is given. The
    // Domain lets only the linked owner act as Owner, so a different actor is declared as an individual representative.
    private async Task<ListingSubmission> SeedAsync(
        Guid actingUserId,
        State state,
        ListingPurpose purpose = ListingPurpose.Sale,
        int minute = 1,
        Guid? linkedUserId = null)
    {
        await using var context = _fixture.CreateContext();
        var property = new Property(Guid.NewGuid(), PropertyType.Apartment, new PropertyAddress("EG"), T0);
        var listing = new PropertyListing(Guid.NewGuid(), property.Id, purpose, T0);
        context.Add(property);
        context.Add(listing);
        await context.SaveChangesAsync();

        var at = T0.AddMinutes(minute);
        var linkedOwner = linkedUserId ?? actingUserId;
        var representation = linkedOwner == actingUserId
            ? ListingRepresentationKind.Owner
            : ListingRepresentationKind.IndividualRepresentative;
        var authority = new ListingLegalAuthority(listing.PropertyId, listing.Purpose,
            new ListingOwnerIdentity(ListingOwnerKind.Individual, Guid.NewGuid(), linkedOwner), actingUserId,
            representation, null, listing.CreatedAt, null);
        var evidence = new ListingEvidenceReference(Guid.NewGuid(), Guid.NewGuid(), listing.PropertyId, listing.Purpose, new string('A', 64));
        var content = new ListingSubmissionContent("Country", "Region", "City", "District", "Title", "Description",
            PropertyType.Apartment, listing.Purpose, 100m, "USD", listing.Purpose == ListingPurpose.Rent ? RentPeriod.Monthly : null);
        var submission = new ListingSubmission(Guid.NewGuid(), listing, content, authority, [evidence], at, null);

        if (state != State.Draft)
        {
            submission.Submit(actingUserId, null, at, Assessments(submission, at), true, null);
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
            submission.Reject(Guid.NewGuid(), Reviewer(submission), RejectionReason, decidedAt);
            await context.SaveChangesAsync();
        }

        return submission;
    }

    private static ListingReviewContext Reviewer(ListingSubmission submission) => new(Guid.NewGuid(),
        submission.Authority.ActingUserId, submission.Authority.DeclaredOwner, submission.Authority.WorkspaceId,
        true, true, submission.Authority.WorkspaceId.HasValue ? false : null, false);

    private static ListingEvidenceAssessment[] Assessments(ListingSubmission submission, DateTimeOffset at) =>
        submission.Evidence.Select(e => new ListingEvidenceAssessment(Guid.NewGuid(), e, EvidenceScanStatus.Clean, at)).ToArray();

    private static Guid[] Ids(JsonDocument document) =>
        document.RootElement.GetProperty("items").EnumerateArray()
            .Select(item => item.GetProperty("submissionId").GetGuid())
            .ToArray();

    private static async Task AssertAccountUnavailableAsync(HttpResponseMessage response)
    {
        using (response)
        {
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.Equal("authentication.account_unavailable", document.RootElement.GetProperty("code").GetString());
        }
    }

    private static async Task<string> WithoutCorrelationAsync(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        return string.Join(
            "|",
            document.RootElement.EnumerateObject()
                .Where(property => property.Name != "correlationId")
                .Select(property => $"{property.Name}={property.Value.GetRawText()}"));
    }

    private static void AssertNoForbiddenTerms(string json)
    {
        using var document = JsonDocument.Parse(json);
        var names = new List<string>();
        CollectNames(document.RootElement, names);

        foreach (var name in names)
        {
            foreach (var term in ForbiddenTerms)
            {
                Assert.False(
                    name.Contains(term, StringComparison.OrdinalIgnoreCase),
                    $"The property '{name}' contains the forbidden term '{term}'.");
            }
        }
    }

    private static void CollectNames(JsonElement element, List<string> names)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                names.Add(property.Name);
                CollectNames(property.Value, names);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                CollectNames(item, names);
            }
        }
    }
}
