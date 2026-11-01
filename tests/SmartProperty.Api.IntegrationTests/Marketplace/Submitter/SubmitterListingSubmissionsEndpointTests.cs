using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using SmartProperty.Api.IntegrationTests.Infrastructure;
using SmartProperty.Application.Marketplace.ListingSubmissions;
using SmartProperty.Application.Marketplace.PublicListings;
using SmartProperty.Domain.Identity;
using SmartProperty.Domain.Marketplace;
using SmartProperty.Domain.PropertyRegistry;
using Xunit;

namespace SmartProperty.Api.IntegrationTests.Marketplace.Submitter;

/// <summary>
/// The submitter read routes through the real host, with the controller mounted explicitly and a test-only stub in
/// place of the read repository that has no production implementation yet.
/// </summary>
/// <remarks>
/// These tests prove how the API presents what a repository returns: authentication, whose submissions are read,
/// routing, binding, error bodies and the submitter JSON shape. They deliberately say nothing about whether the real
/// database query is correct. The stub applies the ownership rule the real query must apply, so a controller or
/// handler that used any user other than the authenticated one would be caught here. The host needs no database.
/// </remarks>
public sealed class SubmitterListingSubmissionsEndpointTests
{
    private const string ListRoute = "/api/marketplace/me/listing-submissions";

    private static readonly Guid Me = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid Other = Guid.Parse("99999999-9999-9999-9999-999999999999");
    private static readonly DateTimeOffset Created = new(2026, 10, 1, 8, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Submitted = new(2026, 10, 5, 8, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Decided = new(2026, 10, 9, 8, 0, 0, TimeSpan.Zero);

    private static readonly string[] ForbiddenTerms =
    [
        "propertyId", "legalParty", "linkedUser", "actingUser", "userId", "submitterId", "workspace", "evidence", "sha256",
        "digest", "assessment", "snapshot", "reviewer", "owner", "grant", "independ", "verif",
    ];

    // ----- the account behind a valid token

    [Theory]
    [InlineData(UserStatus.Pending)]
    [InlineData(UserStatus.Suspended)]
    [InlineData(UserStatus.Deactivated)]
    public async Task AValidTokenForANonActiveAccountIsA403AccountUnavailableOnBothRoutes(UserStatus status)
    {
        var id = Guid.NewGuid();
        using var host = new SubmitterApiTestHost(new StubSubmitterListingSubmissionReadRepository());
        host.Accounts.Set(Me, status);
        host.Stub.Add(Me, Pending(id));
        using var client = host.CreateClientFor(Me);

        using var list = await client.GetAsync(ListRoute);
        using var detail = await client.GetAsync($"{ListRoute}/{id}");

        foreach (var response in new[] { list, detail })
        {
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            var body = await response.Content.ReadAsStringAsync();
            using var document = JsonDocument.Parse(body);
            Assert.Equal("authentication.account_unavailable", document.RootElement.GetProperty("code").GetString());
            Assert.Equal(403, document.RootElement.GetProperty("status").GetInt32());

            // One error for every non-active status: the exact status is not disclosed.
            Assert.DoesNotContain(status.ToString(), body, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Two bedroom apartment", body, StringComparison.Ordinal);
        }

        Assert.Equal(0, host.Stub.ListCallCount);
        Assert.Equal(0, host.Stub.GetCallCount);
    }

    [Fact]
    public async Task AValidTokenForAnAccountThatNoLongerExistsIsA401OnBothRoutes()
    {
        using var host = new SubmitterApiTestHost(new StubSubmitterListingSubmissionReadRepository());
        host.Accounts.Set(Me, null);
        using var client = host.CreateClientFor(Me);

        using var list = await client.GetAsync(ListRoute);
        using var detail = await client.GetAsync($"{ListRoute}/{Guid.NewGuid()}");

        foreach (var response in new[] { list, detail })
        {
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.Equal("authentication.unauthorized", document.RootElement.GetProperty("code").GetString());
            Assert.Equal(401, document.RootElement.GetProperty("status").GetInt32());
        }

        Assert.Equal(0, host.Stub.ListCallCount);
        Assert.Equal(0, host.Stub.GetCallCount);
    }

    [Fact]
    public async Task TheAccountStatusIsReadFromTheRecordOnEveryRequestAndNeverFromTheToken()
    {
        var id = Guid.NewGuid();
        using var host = new SubmitterApiTestHost(new StubSubmitterListingSubmissionReadRepository());
        host.Stub.Add(Me, Pending(id));
        using var client = host.CreateClientFor(Me);

        // One token, three requests, three different persisted states.
        using var whileActive = await client.GetAsync($"{ListRoute}/{id}");
        host.Accounts.Set(Me, UserStatus.Suspended);
        using var afterSuspension = await client.GetAsync($"{ListRoute}/{id}");
        host.Accounts.Set(Me, UserStatus.Active);
        using var afterReactivation = await client.GetAsync($"{ListRoute}/{id}");

        Assert.Equal(HttpStatusCode.OK, whileActive.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, afterSuspension.StatusCode);
        Assert.Equal(HttpStatusCode.OK, afterReactivation.StatusCode);
        Assert.Equal(3, host.Accounts.GetByIdCallCount);
        Assert.Equal(2, host.Stub.GetCallCount);
    }

    [Fact]
    public async Task OneUsersStatusNeverAffectsAnotherUsersRequest()
    {
        using var host = new SubmitterApiTestHost(new StubSubmitterListingSubmissionReadRepository());
        host.Stub.Add(Me, Pending(Guid.NewGuid()));
        host.Accounts.Set(Other, UserStatus.Suspended);
        using var mine = host.CreateClientFor(Me);
        using var theirs = host.CreateClientFor(Other);

        using var mineResponse = await mine.GetAsync(ListRoute);
        using var theirsResponse = await theirs.GetAsync(ListRoute);

        Assert.Equal(HttpStatusCode.OK, mineResponse.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, theirsResponse.StatusCode);
        Assert.Equal(Other, host.Accounts.LastRequestedId);
    }

    [Fact]
    public async Task TheAccountIsLookedUpForTheAuthenticatedUserNeverAClientSuppliedOne()
    {
        using var host = new SubmitterApiTestHost(new StubSubmitterListingSubmissionReadRepository());
        host.Accounts.Set(Other, UserStatus.Suspended);
        using var client = host.CreateClientFor(Me);

        using var response = await client.GetAsync($"{ListRoute}?userId={Other}&submitterId={Other}&actingUserId={Other}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(Me, host.Accounts.LastRequestedId);
    }

    [Fact]
    public async Task AnUnauthenticatedRequestNeverReachesTheAccountLookup()
    {
        using var host = new SubmitterApiTestHost(new StubSubmitterListingSubmissionReadRepository());
        using var client = host.CreateClient();

        using var response = await client.GetAsync(ListRoute);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(0, host.Accounts.GetByIdCallCount);
    }

    // ----- authentication

    [Fact]
    public async Task AnAnonymousListIsA401WithTheStandardBodyAndTheRepositoryIsNeverRead()
    {
        using var host = new SubmitterApiTestHost(new StubSubmitterListingSubmissionReadRepository());
        using var client = host.CreateClient();

        using var response = await client.GetAsync(ListRoute);

        await AssertUnauthorizedAsync(response);
        Assert.Equal(0, host.Stub.ListCallCount);
    }

    [Fact]
    public async Task AnAnonymousDetailIsA401AndTheRepositoryIsNeverRead()
    {
        using var host = new SubmitterApiTestHost(new StubSubmitterListingSubmissionReadRepository());
        using var client = host.CreateClient();

        using var response = await client.GetAsync($"{ListRoute}/{Guid.NewGuid()}");

        await AssertUnauthorizedAsync(response);
        Assert.Equal(0, host.Stub.GetCallCount);
    }

    [Fact]
    public async Task AGarbageOrExpiredTokenIsA401()
    {
        using var host = new SubmitterApiTestHost(new StubSubmitterListingSubmissionReadRepository());
        using var garbage = host.CreateClient();
        garbage.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "not-a-token");
        using var expired = host.CreateClient();
        expired.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", TestJwt.CreateExpiredAccessToken(Me));

        using var garbageResponse = await garbage.GetAsync(ListRoute);
        using var expiredResponse = await expired.GetAsync(ListRoute);

        await AssertUnauthorizedAsync(garbageResponse);
        await AssertUnauthorizedAsync(expiredResponse);
        Assert.Equal(0, host.Stub.ListCallCount);
    }

    // ----- list

    [Fact]
    public async Task AnAuthenticatedListReturns200WithOnlyTheCallersSubmissionsInTheSubmitterShape()
    {
        using var host = new SubmitterApiTestHost(new StubSubmitterListingSubmissionReadRepository());
        host.Stub.Add(Me, Pending(Guid.NewGuid(), updatedAt: Submitted));
        host.Stub.Add(Me, Draft(Guid.NewGuid()));
        host.Stub.Add(Other, Pending(Guid.NewGuid(), title: "SENTINEL-OTHER-USER"));
        using var client = host.CreateClientFor(Me);

        using var response = await client.GetAsync(ListRoute);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        Assert.True(response.Headers.Contains("X-Correlation-ID"));
        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("SENTINEL-OTHER-USER", body, StringComparison.Ordinal);
        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;
        Assert.Equal(
            new[] { "hasNext", "hasPrevious", "items", "page", "pageSize", "totalCount", "totalPages" },
            SortedNames(root));
        Assert.Equal(2, root.GetProperty("totalCount").GetInt64());
        Assert.Equal(1, root.GetProperty("page").GetInt32());
        Assert.Equal(10, root.GetProperty("pageSize").GetInt32());

        var items = root.GetProperty("items");
        Assert.Equal(2, items.GetArrayLength());
        Assert.Equal(
            new[]
            {
                "createdAt", "isCurrentlyPublished", "listingId", "location", "price", "propertyType", "purpose",
                "representation", "status", "submissionId", "submittedAt", "title", "updatedAt", "version",
            },
            SortedNames(items[0]));
        Assert.Equal("PendingReview", items[0].GetProperty("status").GetString());

        // A Draft has no submission time, so the key is omitted rather than null.
        Assert.Equal("Draft", items[1].GetProperty("status").GetString());
        Assert.DoesNotContain("submittedAt", SortedNames(items[1]));
    }

    [Fact]
    public async Task TheCallerOnlyEverReachesTheRepositoryAsTheAuthenticatedUser()
    {
        using var host = new SubmitterApiTestHost(new StubSubmitterListingSubmissionReadRepository());
        host.Stub.Add(Me, Pending(Guid.NewGuid()));
        host.Stub.Add(Other, Pending(Guid.NewGuid(), title: "SENTINEL-OTHER-USER"));
        using var client = host.CreateClientFor(Me);

        // Every plausible spelling of a client-supplied identity is an unknown key and is ignored.
        var query = $"?userId={Other}&submitterId={Other}&actingUserId={Other}&ownerId={Other}&user={Other}";
        using var response = await client.GetAsync($"{ListRoute}{query}");
        using var detailResponse = await client.GetAsync($"{ListRoute}/{Guid.NewGuid()}{query}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(Me, host.Stub.LastListCall!.ActingUserId);
        Assert.Equal(Me, host.Stub.LastGetCall!.ActingUserId);
        Assert.DoesNotContain("SENTINEL-OTHER-USER", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.NotFound, detailResponse.StatusCode);
    }

    [Fact]
    public async Task AUserWithNoSubmissionsGetsAnEmptyPage()
    {
        using var host = new SubmitterApiTestHost(new StubSubmitterListingSubmissionReadRepository());
        host.Stub.Add(Other, Pending(Guid.NewGuid()));
        using var client = host.CreateClientFor(Me);

        using var response = await client.GetAsync(ListRoute);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(0, document.RootElement.GetProperty("items").GetArrayLength());
        Assert.Equal(0, document.RootElement.GetProperty("totalCount").GetInt64());
    }

    [Fact]
    public async Task EveryStateIsRepresentedFaithfullyInTheList()
    {
        using var host = new SubmitterApiTestHost(new StubSubmitterListingSubmissionReadRepository());
        host.Stub.Add(Me, Draft(Guid.NewGuid(), updatedAt: Created.AddHours(1)));
        host.Stub.Add(Me, Pending(Guid.NewGuid(), updatedAt: Submitted));
        host.Stub.Add(Me, Approved(Guid.NewGuid(), published: true, updatedAt: Decided));
        host.Stub.Add(Me, Rejected(Guid.NewGuid(), updatedAt: Decided.AddDays(1)));
        host.Stub.Add(Me, Superseded(Guid.NewGuid(), updatedAt: Decided.AddDays(2)));
        using var client = host.CreateClientFor(Me);

        using var response = await client.GetAsync(ListRoute);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var items = document.RootElement.GetProperty("items").EnumerateArray().ToArray();
        Assert.Equal(
            new[] { "Superseded", "Rejected", "Approved", "PendingReview", "Draft" },
            items.Select(item => item.GetProperty("status").GetString()!).ToArray());
        Assert.Equal(
            new[] { false, false, true, false, false },
            items.Select(item => item.GetProperty("isCurrentlyPublished").GetBoolean()).ToArray());
    }

    // ----- paging and filters

    [Fact]
    public async Task PagingPassesTheClampedWindowAndReportsTheEnvelope()
    {
        using var host = new SubmitterApiTestHost(new StubSubmitterListingSubmissionReadRepository());
        for (var i = 0; i < 12; i++)
        {
            host.Stub.Add(Me, Pending(Guid.NewGuid(), updatedAt: Submitted.AddMinutes(i)));
        }

        using var client = host.CreateClientFor(Me);

        using var response = await client.GetAsync($"{ListRoute}?page=2&pageSize=5");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = document.RootElement;
        Assert.Equal(5, root.GetProperty("items").GetArrayLength());
        Assert.Equal(2, root.GetProperty("page").GetInt32());
        Assert.Equal(5, root.GetProperty("pageSize").GetInt32());
        Assert.Equal(12, root.GetProperty("totalCount").GetInt64());
        Assert.Equal(3, root.GetProperty("totalPages").GetInt32());
        Assert.True(root.GetProperty("hasNext").GetBoolean());
        Assert.True(root.GetProperty("hasPrevious").GetBoolean());
        Assert.Equal(5, host.Stub.LastListCall!.Skip);
        Assert.Equal(5, host.Stub.LastListCall.Take);
    }

    [Theory]
    [InlineData("pageSize=1000", 0, 100)]
    [InlineData("pageSize=0", 0, 1)]
    [InlineData("page=0", 0, 10)]
    [InlineData("page=-3", 0, 10)]
    [InlineData("page=3&pageSize=20", 40, 20)]
    public async Task OutOfRangePagingIsClampedNotRejected(string query, int expectedSkip, int expectedTake)
    {
        using var host = new SubmitterApiTestHost(new StubSubmitterListingSubmissionReadRepository());
        using var client = host.CreateClientFor(Me);

        using var response = await client.GetAsync($"{ListRoute}?{query}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(expectedSkip, host.Stub.LastListCall!.Skip);
        Assert.Equal(expectedTake, host.Stub.LastListCall.Take);
    }

    [Fact]
    public async Task PurposeAndStatusFiltersReachTheRepositoryAndNarrowTheResult()
    {
        using var host = new SubmitterApiTestHost(new StubSubmitterListingSubmissionReadRepository());
        host.Stub.Add(Me, Pending(Guid.NewGuid(), purpose: ListingPurpose.Sale));
        host.Stub.Add(Me, Pending(Guid.NewGuid(), purpose: ListingPurpose.Rent, title: "A rental"));
        host.Stub.Add(Me, Draft(Guid.NewGuid(), purpose: ListingPurpose.Rent, title: "A rental draft"));
        using var client = host.CreateClientFor(Me);

        using var rentals = await client.GetAsync($"{ListRoute}?purpose=Rent");
        using var rentalDrafts = await client.GetAsync($"{ListRoute}?purpose=rent&status=draft");

        Assert.Equal(HttpStatusCode.OK, rentals.StatusCode);
        using var rentalDocument = JsonDocument.Parse(await rentals.Content.ReadAsStringAsync());
        Assert.Equal(2, rentalDocument.RootElement.GetProperty("totalCount").GetInt64());

        Assert.Equal(HttpStatusCode.OK, rentalDrafts.StatusCode);
        using var draftDocument = JsonDocument.Parse(await rentalDrafts.Content.ReadAsStringAsync());
        var item = Assert.Single(draftDocument.RootElement.GetProperty("items").EnumerateArray());
        Assert.Equal("A rental draft", item.GetProperty("title").GetString());
        Assert.Equal(ListingPurpose.Rent, host.Stub.LastListCall!.Purpose);
        Assert.Equal(ListingSubmissionStatus.Draft, host.Stub.LastListCall.Status);
    }

    // ----- validation

    [Theory]
    [InlineData("purpose=Auction")]
    [InlineData("status=Bogus")]
    [InlineData("page=abc")]
    [InlineData("pageSize=abc")]
    [InlineData("purpose=99")]
    [InlineData("status=99")]
    [InlineData("status=0")]
    public async Task AnUnparseableOrUndefinedValueIsA400MalformedRequestAndNeverReachesTheRepository(string query)
    {
        using var host = new SubmitterApiTestHost(new StubSubmitterListingSubmissionReadRepository());
        using var client = host.CreateClientFor(Me);

        using var response = await client.GetAsync($"{ListRoute}?{query}");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("request.malformed", document.RootElement.GetProperty("code").GetString());
        Assert.Equal(400, document.RootElement.GetProperty("status").GetInt32());
        Assert.Equal(0, host.Stub.ListCallCount);
    }

    // ----- detail

    [Fact]
    public async Task ARejectedDetailShowsItsReasonAndNothingPrivate()
    {
        var id = Guid.NewGuid();
        using var host = new SubmitterApiTestHost(new StubSubmitterListingSubmissionReadRepository());
        host.Stub.Add(Me, Rejected(id, updatedAt: Decided));
        using var client = host.CreateClientFor(Me);

        using var response = await client.GetAsync($"{ListRoute}/{id}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;
        Assert.Equal(
            new[]
            {
                "createdAt", "decision", "description", "isCurrentlyPublished", "listingId", "location", "price",
                "propertyType", "purpose", "representation", "status", "submissionId", "submittedAt", "title",
                "updatedAt", "version",
            },
            SortedNames(root));
        Assert.Equal(id, root.GetProperty("submissionId").GetGuid());
        Assert.Equal("Rejected", root.GetProperty("status").GetString());
        var decision = root.GetProperty("decision");
        Assert.Equal(new[] { "approved", "decidedAt", "reason" }, SortedNames(decision));
        Assert.False(decision.GetProperty("approved").GetBoolean());
        Assert.Equal("The deed does not cover this parcel.", decision.GetProperty("reason").GetString());
        AssertNoForbiddenTerms(body);
        Assert.Equal(Me, host.Stub.LastGetCall!.ActingUserId);
        Assert.Equal(id, host.Stub.LastGetCall.SubmissionId);
    }

    [Fact]
    public async Task AnApprovedDetailNeverShowsAReasonWhateverIsStored()
    {
        var id = Guid.NewGuid();
        using var host = new SubmitterApiTestHost(new StubSubmitterListingSubmissionReadRepository());
        host.Stub.Add(Me, Approved(id, published: true, updatedAt: Decided, storedReason: "SENTINEL-INTERNAL-NOTE"));
        using var client = host.CreateClientFor(Me);

        using var response = await client.GetAsync($"{ListRoute}/{id}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("SENTINEL-INTERNAL-NOTE", body, StringComparison.Ordinal);
        using var document = JsonDocument.Parse(body);
        var decision = document.RootElement.GetProperty("decision");
        Assert.Equal(new[] { "approved", "decidedAt" }, SortedNames(decision));
        Assert.True(decision.GetProperty("approved").GetBoolean());
        Assert.True(document.RootElement.GetProperty("isCurrentlyPublished").GetBoolean());
    }

    [Fact]
    public async Task ADraftDetailHasNeitherASubmissionTimeNorADecision()
    {
        var id = Guid.NewGuid();
        using var host = new SubmitterApiTestHost(new StubSubmitterListingSubmissionReadRepository());
        host.Stub.Add(Me, Draft(id));
        using var client = host.CreateClientFor(Me);

        using var response = await client.GetAsync($"{ListRoute}/{id}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var keys = SortedNames(document.RootElement);
        Assert.DoesNotContain("submittedAt", keys);
        Assert.DoesNotContain("decision", keys);
        Assert.Equal("Draft", document.RootElement.GetProperty("status").GetString());
    }

    [Fact]
    public async Task ARentalDetailCarriesItsRentPeriodAndASaleDoesNot()
    {
        var rental = Guid.NewGuid();
        var sale = Guid.NewGuid();
        using var host = new SubmitterApiTestHost(new StubSubmitterListingSubmissionReadRepository());
        host.Stub.Add(Me, Pending(rental, purpose: ListingPurpose.Rent));
        host.Stub.Add(Me, Pending(sale, purpose: ListingPurpose.Sale));
        using var client = host.CreateClientFor(Me);

        using var rentalResponse = await client.GetAsync($"{ListRoute}/{rental}");
        using var saleResponse = await client.GetAsync($"{ListRoute}/{sale}");

        using var rentalDocument = JsonDocument.Parse(await rentalResponse.Content.ReadAsStringAsync());
        using var saleDocument = JsonDocument.Parse(await saleResponse.Content.ReadAsStringAsync());
        Assert.Equal("Monthly", rentalDocument.RootElement.GetProperty("price").GetProperty("rentPeriod").GetString());
        Assert.DoesNotContain("rentPeriod", SortedNames(saleDocument.RootElement.GetProperty("price")));
    }

    [Fact]
    public async Task AMissingSubmissionAndAnotherUsersSubmissionAreIndistinguishable404s()
    {
        var theirs = Guid.NewGuid();
        using var host = new SubmitterApiTestHost(new StubSubmitterListingSubmissionReadRepository());
        host.Stub.Add(Other, Pending(theirs, title: "SENTINEL-OTHER-USER"));
        using var client = host.CreateClientFor(Me);

        using var missing = await client.GetAsync($"{ListRoute}/{Guid.NewGuid()}");
        using var foreign = await client.GetAsync($"{ListRoute}/{theirs}");

        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, foreign.StatusCode);
        var missingBody = await missing.Content.ReadAsStringAsync();
        var foreignBody = await foreign.Content.ReadAsStringAsync();
        Assert.DoesNotContain("SENTINEL-OTHER-USER", foreignBody, StringComparison.Ordinal);
        Assert.DoesNotContain(theirs.ToString(), foreignBody, StringComparison.OrdinalIgnoreCase);

        using var missingDocument = JsonDocument.Parse(missingBody);
        using var foreignDocument = JsonDocument.Parse(foreignBody);
        Assert.Equal("listing_submissions.not_found", missingDocument.RootElement.GetProperty("code").GetString());
        Assert.Equal("listing_submissions.not_found", foreignDocument.RootElement.GetProperty("code").GetString());
        Assert.Equal(
            missingDocument.RootElement.GetProperty("message").GetString(),
            foreignDocument.RootElement.GetProperty("message").GetString());
        Assert.Equal(404, foreignDocument.RootElement.GetProperty("status").GetInt32());
    }

    [Fact]
    public async Task TheEmptyGuidIsA404WithoutAskingTheRepository()
    {
        using var host = new SubmitterApiTestHost(new StubSubmitterListingSubmissionReadRepository());
        using var client = host.CreateClientFor(Me);

        using var response = await client.GetAsync($"{ListRoute}/{Guid.Empty}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(0, host.Stub.GetCallCount);
    }

    [Fact]
    public async Task ANonGuidIdMatchesNoRouteAndNeverReachesTheRepository()
    {
        using var host = new SubmitterApiTestHost(new StubSubmitterListingSubmissionReadRepository());
        using var client = host.CreateClientFor(Me);

        using var response = await client.GetAsync($"{ListRoute}/not-a-guid");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(0, host.Stub.GetCallCount);
    }

    // ----- failures

    [Fact]
    public async Task ARepositoryFailureIsAGeneric500ThatLeaksNothing()
    {
        using var host = new SubmitterApiTestHost(new StubSubmitterListingSubmissionReadRepository
        {
            ExceptionToThrow = new InvalidOperationException("SENTINEL-DATABASE-DETAIL"),
        });
        using var client = host.CreateClientFor(Me);

        using var list = await client.GetAsync(ListRoute);
        using var detail = await client.GetAsync($"{ListRoute}/{Guid.NewGuid()}");

        foreach (var response in new[] { list, detail })
        {
            Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
            var body = await response.Content.ReadAsStringAsync();
            using var document = JsonDocument.Parse(body);
            Assert.Equal("server.unexpected_error", document.RootElement.GetProperty("code").GetString());
            Assert.DoesNotContain("SENTINEL", body, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task ACorruptRowIsAGeneric500ThatLeaksNothingAndIsNeverSkipped()
    {
        var corrupt = Guid.NewGuid();
        using var host = new SubmitterApiTestHost(new StubSubmitterListingSubmissionReadRepository());
        host.Stub.Add(Me, Pending(Guid.NewGuid()));
        host.Stub.Add(Me, Pending(corrupt, title: "SENTINEL-TITLE") with { Version = 0 });
        using var client = host.CreateClientFor(Me);

        using var list = await client.GetAsync(ListRoute);
        using var detail = await client.GetAsync($"{ListRoute}/{corrupt}");

        foreach (var response in new[] { list, detail })
        {
            Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
            var body = await response.Content.ReadAsStringAsync();
            using var document = JsonDocument.Parse(body);
            Assert.Equal("server.unexpected_error", document.RootElement.GetProperty("code").GetString());
            Assert.DoesNotContain("SENTINEL", body, StringComparison.Ordinal);
        }
    }

    // ----- routing and production reachability

    [Fact]
    public async Task OnlyGetIsAllowedOnBothRoutes()
    {
        var id = Guid.NewGuid();
        using var host = new SubmitterApiTestHost(new StubSubmitterListingSubmissionReadRepository());
        host.Stub.Add(Me, Pending(id));
        using var client = host.CreateClientFor(Me);

        foreach (var method in new[] { HttpMethod.Post, HttpMethod.Put, HttpMethod.Delete, HttpMethod.Patch })
        {
            using var list = await client.SendAsync(new HttpRequestMessage(method, ListRoute));
            using var detail = await client.SendAsync(new HttpRequestMessage(method, $"{ListRoute}/{id}"));

            Assert.Equal(HttpStatusCode.MethodNotAllowed, list.StatusCode);
            Assert.Equal(HttpStatusCode.MethodNotAllowed, detail.StatusCode);
        }
    }

    [Fact]
    public void TheMountedHostExposesExactlyTheTwoGetRoutesUnderTheSubmitterPath()
    {
        using var host = new SubmitterApiTestHost(new StubSubmitterListingSubmissionReadRepository());

        var endpoints = host.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>()
            .Where(endpoint => (endpoint.RoutePattern.RawText ?? string.Empty).StartsWith("api/marketplace/me", StringComparison.OrdinalIgnoreCase))
            .ToArray();

        Assert.Equal(
            new[] { "api/marketplace/me/listing-submissions", "api/marketplace/me/listing-submissions/{submissionId:guid}" },
            endpoints.Select(endpoint => endpoint.RoutePattern.RawText!).Order(StringComparer.Ordinal).ToArray());
        Assert.All(
            endpoints,
            endpoint => Assert.Equal(
                new[] { "GET" },
                endpoint.Metadata.GetMetadata<HttpMethodMetadata>()!.HttpMethods.ToArray()));
    }

    [Fact]
    public async Task TheProductionCompositionHasNoSubmitterRouteAtAll()
    {
        using var host = new SubmitterApiTestHost(new StubSubmitterListingSubmissionReadRepository(), mount: false);
        using var client = host.CreateClientFor(Me);

        var routes = host.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>()
            .Select(endpoint => endpoint.RoutePattern.RawText ?? string.Empty)
            .ToArray();
        using var list = await client.GetAsync(ListRoute);
        using var detail = await client.GetAsync($"{ListRoute}/{Guid.NewGuid()}");

        // The inventory is real: existing routes are visible to it.
        Assert.Contains(routes, route => route.StartsWith("api/public/marketplace/listings", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(routes, route => route.Contains("marketplace/me", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(HttpStatusCode.NotFound, list.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, detail.StatusCode);
        Assert.Equal(0, host.Stub.ListCallCount);
        Assert.Equal(0, host.Stub.GetCallCount);
    }

    // ----- helpers

    private static async Task AssertUnauthorizedAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Contains("Bearer", response.Headers.WwwAuthenticate.Select(header => header.Scheme));
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("authentication.unauthorized", document.RootElement.GetProperty("code").GetString());
        Assert.Equal(401, document.RootElement.GetProperty("status").GetInt32());
    }

    // Checks the JSON property names, not the values: a submitter's own declared representation, "Owner", is a
    // legitimate value, while no property may be named after a party, a user, a Workspace or a piece of evidence.
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

    private static string[] SortedNames(JsonElement element)
    {
        return element.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal).ToArray();
    }

    private static SubmitterSubmissionDetail Pending(
        Guid id,
        ListingPurpose purpose = ListingPurpose.Sale,
        string title = "Two bedroom apartment",
        DateTimeOffset? updatedAt = null)
    {
        return new SubmitterSubmissionDetail(
            id,
            Guid.NewGuid(),
            purpose,
            1,
            ListingSubmissionStatus.PendingReview,
            title,
            "Close to the centre.",
            purpose == ListingPurpose.Rent
                ? new PublicListingPrice(5200m, "SAR", RentPeriod.Monthly)
                : new PublicListingPrice(1250000m, "SAR", null),
            PropertyType.Unit,
            new PublicListingLocation("SA", "Riyadh Province", "Riyadh", "Al Olaya"),
            ListingRepresentationKind.Owner,
            Created,
            Submitted,
            updatedAt ?? Submitted,
            false,
            null);
    }

    private static SubmitterSubmissionDetail Draft(
        Guid id,
        ListingPurpose purpose = ListingPurpose.Sale,
        string title = "Two bedroom apartment",
        DateTimeOffset? updatedAt = null)
    {
        return Pending(id, purpose, title) with
        {
            Status = ListingSubmissionStatus.Draft,
            SubmittedAt = null,
            UpdatedAt = updatedAt ?? Created.AddHours(1),
        };
    }

    private static SubmitterSubmissionDetail Approved(Guid id, bool published, DateTimeOffset updatedAt, string? storedReason = null)
    {
        return Pending(id) with
        {
            Status = ListingSubmissionStatus.Approved,
            UpdatedAt = updatedAt,
            IsCurrentlyPublished = published,
            Decision = new SubmitterSubmissionDecision(Approved: true, storedReason, Decided),
        };
    }

    private static SubmitterSubmissionDetail Rejected(Guid id, DateTimeOffset updatedAt)
    {
        return Pending(id) with
        {
            Status = ListingSubmissionStatus.Rejected,
            UpdatedAt = updatedAt,
            Decision = new SubmitterSubmissionDecision(Approved: false, "The deed does not cover this parcel.", Decided),
        };
    }

    private static SubmitterSubmissionDetail Superseded(Guid id, DateTimeOffset updatedAt)
    {
        return Pending(id) with
        {
            Status = ListingSubmissionStatus.Superseded,
            UpdatedAt = updatedAt,
            Decision = new SubmitterSubmissionDecision(Approved: true, null, Decided),
        };
    }
}
