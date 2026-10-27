using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using SmartProperty.Api.Controllers;
using SmartProperty.Api.Infrastructure.Authorization;
using SmartProperty.Api.IntegrationTests.Infrastructure;
using SmartProperty.Application.Abstractions.Authorization;
using SmartProperty.Application.Abstractions.Messaging;
using SmartProperty.Application.Abstractions.Persistence.Repositories;
using SmartProperty.Application.Authorization;
using SmartProperty.Application.Marketplace.ListingSubmissions.Review;
using SmartProperty.Application.Marketplace.ListingSubmissions.Review.Detail;
using SmartProperty.Application.Marketplace.ListingSubmissions.Review.Queue;
using SmartProperty.Application.Marketplace.PublicListings;
using SmartProperty.Common.Pagination;
using SmartProperty.Domain.Marketplace;
using SmartProperty.Domain.PropertyRegistry;
using Xunit;

namespace SmartProperty.Api.IntegrationTests.Marketplace.Admin;

/// <summary>
/// The Platform Admin review reads through the real host, with the internal controller mounted explicitly and a
/// test-only stub in place of the reviewer read repository that has no production implementation yet.
/// </summary>
/// <remarks>
/// These tests prove how the API presents what a repository returns: authentication, the exact Platform review
/// permission, who is reviewing, independence outcomes, routing, binding, error bodies and the reviewer JSON shape.
/// They deliberately say nothing about whether the real database query is correct. The stub applies the independence
/// rule the real query must apply, so a controller or handler that used any reviewer other than the authenticated one
/// would be caught here. The host needs no database.
/// </remarks>
public sealed class AdminListingSubmissionReviewEndpointTests
{
    private const string Route = "/api/admin/marketplace/listing-submissions";

    private static readonly Guid Reviewer = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid Submitter = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid LinkedOwner = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly DateTimeOffset Created = new(2026, 10, 1, 8, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Submitted = new(2026, 10, 5, 8, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Decided = new(2026, 10, 9, 8, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset ValidFrom = new(2026, 9, 1, 0, 0, 0, TimeSpan.Zero);

    // Matched against JSON property NAMES, never values: a reviewer legitimately sees the owner kind "Individual".
    private static readonly string[] ForbiddenNameTerms =
    [
        "legalparty", "linkeduser", "declaredowner", "actinguser", "submitteruser", "userid", "reviewer", "workspace",
        "sha", "digest", "hash", "assessment", "snapshot", "verif", "scan", "independ", "conflict", "affiliat",
        "concurrency", "storage", "email", "phone", "token", "secret", "password", "uri", "url", "raw", "json",
    ];

    // ----- authentication

    [Fact]
    public async Task AnAnonymousListIsA401WithTheStandardBodyAndNothingIsAskedOrRead()
    {
        using var host = new AdminReviewApiTestHost(new StubReviewerListingSubmissionReadRepository());
        using var client = host.CreateClient();

        using var response = await client.GetAsync(Route);

        await AssertUnauthorizedAsync(response);
        Assert.Equal(0, host.Permissions.CallCount);
        Assert.Equal(0, host.Stub.ListCallCount);
    }

    [Fact]
    public async Task AnAnonymousDetailIsA401AndNothingIsAskedOrRead()
    {
        using var host = new AdminReviewApiTestHost(new StubReviewerListingSubmissionReadRepository());
        using var client = host.CreateClient();

        using var response = await client.GetAsync($"{Route}/{Guid.NewGuid()}");

        await AssertUnauthorizedAsync(response);
        Assert.Equal(0, host.Permissions.CallCount);
        Assert.Equal(0, host.Stub.GetCallCount);
    }

    [Fact]
    public async Task AGarbageOrExpiredTokenIsA401EvenWhenThePermissionWouldBeAllowed()
    {
        using var host = new AdminReviewApiTestHost(new StubReviewerListingSubmissionReadRepository());
        host.Permissions.AllowedPermissionCodes.Add(PermissionCodes.MarketplaceSubmissionsReviewRead);
        using var garbage = host.CreateClient();
        garbage.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "not-a-token");
        using var expired = host.CreateClient();
        expired.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", TestJwt.CreateExpiredAccessToken(Reviewer));

        using var garbageResponse = await garbage.GetAsync(Route);
        using var expiredResponse = await expired.GetAsync(Route);

        await AssertUnauthorizedAsync(garbageResponse);
        await AssertUnauthorizedAsync(expiredResponse);
        Assert.Equal(0, host.Permissions.CallCount);
        Assert.Equal(0, host.Stub.ListCallCount);
    }

    // ----- the exact Platform review permission

    [Fact]
    public async Task AnAuthenticatedUserWithoutThePermissionIsA403OnBothRoutesAndNothingIsRead()
    {
        using var host = new AdminReviewApiTestHost(new StubReviewerListingSubmissionReadRepository());
        host.Stub.Add(Pending(Guid.NewGuid(), title: "SENTINEL-TITLE"), Submitter);
        using var client = host.CreateClientFor(Reviewer);

        using var list = await client.GetAsync(Route);
        using var detail = await client.GetAsync($"{Route}/{Guid.NewGuid()}");

        foreach (var response in new[] { list, detail })
        {
            await AssertForbiddenAsync(response);
        }

        Assert.Equal(0, host.Stub.ListCallCount);
        Assert.Equal(0, host.Stub.GetCallCount);
    }

    [Fact]
    public async Task EveryOtherPermissionIsRefusedIncludingTheAuthorityToDecide()
    {
        using var host = new AdminReviewApiTestHost(new StubReviewerListingSubmissionReadRepository());
        host.Permissions.AllowedPermissionCodes.Add(PermissionCodes.MarketplaceSubmissionsReviewDecide);
        host.Permissions.AllowedPermissionCodes.Add(PermissionCodes.WorkspaceAccessRequestsReview);
        host.Permissions.AllowedPermissionCodes.Add(PermissionCodes.MarketplaceAuthorityGrantsRead);
        using var client = host.CreateClientFor(Reviewer);

        using var list = await client.GetAsync(Route);
        using var detail = await client.GetAsync($"{Route}/{Guid.NewGuid()}");

        await AssertForbiddenAsync(list);
        await AssertForbiddenAsync(detail);
        Assert.Equal(0, host.Stub.ListCallCount);
        Assert.Equal(0, host.Stub.GetCallCount);
    }

    [Fact]
    public async Task ThePermissionIsAskedForAtPlatformScopeForTheAuthenticatedUserAndNothingElseIsConsulted()
    {
        using var host = new AdminReviewApiTestHost(new StubReviewerListingSubmissionReadRepository());
        var id = Guid.NewGuid();
        host.Stub.Add(Pending(id), Submitter);
        using var client = host.CreateReviewerClientFor(Reviewer);

        using var list = await client.GetAsync(Route);
        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        var afterList = host.Permissions.CallCount;
        using var detail = await client.GetAsync($"{Route}/{id}");
        Assert.Equal(HttpStatusCode.OK, detail.StatusCode);

        // The door and the handler each ask, so neither layer is relied on alone.
        Assert.Equal(2, afterList);
        Assert.Equal(4, host.Permissions.CallCount);

        foreach (var request in host.Permissions.Requests)
        {
            Assert.Equal(Reviewer, request.UserId);
            Assert.Equal(PermissionCodes.MarketplaceSubmissionsReviewRead, request.PermissionCode);
            Assert.Same(AuthorizationTarget.Platform, request.Target);
        }
    }

    [Fact]
    public async Task APermissionStoreFailureIsAGeneric500NeverAccessAndNothingIsRead()
    {
        using var host = new AdminReviewApiTestHost(new StubReviewerListingSubmissionReadRepository());
        host.Permissions.ExceptionToThrow = new InvalidOperationException("SENTINEL-PERMISSION-STORE");
        using var client = host.CreateClientFor(Reviewer);

        using var list = await client.GetAsync(Route);
        using var detail = await client.GetAsync($"{Route}/{Guid.NewGuid()}");

        foreach (var response in new[] { list, detail })
        {
            await AssertGenericServerErrorAsync(response);
        }

        Assert.Equal(0, host.Stub.ListCallCount);
        Assert.Equal(0, host.Stub.GetCallCount);
    }

    [Fact]
    public async Task ReviewerAuthorityCannotBeClaimedThroughTheRequest()
    {
        using var host = new AdminReviewApiTestHost(new StubReviewerListingSubmissionReadRepository());
        host.Stub.Add(Pending(Guid.NewGuid(), title: "SENTINEL-TITLE"), Submitter);
        using var client = host.CreateClientFor(Reviewer);
        var query = $"?reviewerUserId={Submitter}&userId={Submitter}&isReviewer=true&isPlatformAdmin=true"
            + $"&permission={PermissionCodes.MarketplaceSubmissionsReviewRead}&role=PlatformAdmin&workspaceId={Guid.NewGuid()}";

        foreach (var path in new[] { Route + query, $"{Route}/{Guid.NewGuid()}{query}" })
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, path);
            request.Headers.Add("X-Reviewer", "true");
            request.Headers.Add("X-User-Id", Submitter.ToString());
            request.Headers.Add("X-Roles", "PlatformAdmin");
            request.Headers.Add("X-Permissions", PermissionCodes.MarketplaceSubmissionsReviewRead);

            using var response = await client.SendAsync(request);

            await AssertForbiddenAsync(response);
        }

        Assert.Equal(0, host.Stub.ListCallCount);
        Assert.Equal(0, host.Stub.GetCallCount);
        Assert.All(host.Permissions.Requests, request => Assert.Equal(Reviewer, request.UserId));
    }

    [Fact]
    public async Task ARequestThatIsNotAllowedLearnsNothingAboutTheQuery()
    {
        using var host = new AdminReviewApiTestHost(new StubReviewerListingSubmissionReadRepository());
        using var client = host.CreateClientFor(Reviewer);

        using var malformed = await client.GetAsync($"{Route}?status=Bogus&page=abc");
        using var draft = await client.GetAsync($"{Route}?status=Draft");

        // Authorization comes first, so a caller who may not read gets the same 403 for any query.
        await AssertForbiddenAsync(malformed);
        await AssertForbiddenAsync(draft);
    }

    [Fact]
    public async Task OnlyTheAuthenticatedUserIsEverTheReviewerTheRepositoryIsToldAbout()
    {
        using var host = new AdminReviewApiTestHost(new StubReviewerListingSubmissionReadRepository());
        var id = Guid.NewGuid();
        host.Stub.Add(Pending(id), Submitter);
        using var client = host.CreateReviewerClientFor(Reviewer);

        using var list = await client.GetAsync($"{Route}?reviewerUserId={Submitter}");
        using var detail = await client.GetAsync($"{Route}/{id}?reviewerUserId={Submitter}");

        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        Assert.Equal(HttpStatusCode.OK, detail.StatusCode);
        Assert.Equal(Reviewer, host.Stub.LastListCall!.ReviewerUserId);
        Assert.Equal(Reviewer, host.Stub.LastGetCall!.ReviewerUserId);
    }

    // ----- the queue

    [Fact]
    public async Task AReviewerListReturns200WithThePendingQueueOldestFirstInTheReviewerShape()
    {
        using var host = new AdminReviewApiTestHost(new StubReviewerListingSubmissionReadRepository());
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        host.Stub.Add(Pending(second, title: "Second", submittedAt: Submitted.AddHours(2)), Submitter);
        host.Stub.Add(Pending(first, title: "First", submittedAt: Submitted), Submitter);
        host.Stub.Add(Approved(Guid.NewGuid(), Decided), Submitter);
        using var client = host.CreateReviewerClientFor(Reviewer);

        using var response = await client.GetAsync(Route);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var items = document.RootElement.GetProperty("items").EnumerateArray().ToArray();
        Assert.Equal(2, items.Length);
        Assert.Equal(first.ToString(), items[0].GetProperty("submissionId").GetString());
        Assert.Equal(second.ToString(), items[1].GetProperty("submissionId").GetString());

        var item = items[0];
        Assert.Equal(
            new[]
            {
                "allEvidenceClean", "evidenceCount", "listingId", "location", "ownerKind", "price", "propertyType",
                "purpose", "representation", "status", "submissionId", "submittedAt", "title", "version",
            },
            SortedNames(item));
        Assert.Equal("First", item.GetProperty("title").GetString());
        Assert.Equal("Sale", item.GetProperty("purpose").GetString());
        Assert.Equal("PendingReview", item.GetProperty("status").GetString());
        Assert.Equal("Unit", item.GetProperty("propertyType").GetString());
        Assert.Equal("Owner", item.GetProperty("representation").GetString());
        Assert.Equal("Individual", item.GetProperty("ownerKind").GetString());
        Assert.Equal(2, item.GetProperty("evidenceCount").GetInt32());
        Assert.True(item.GetProperty("allEvidenceClean").GetBoolean());
        Assert.Equal(1250000m, item.GetProperty("price").GetProperty("amount").GetDecimal());
        Assert.Equal("SAR", item.GetProperty("price").GetProperty("currency").GetString());
        AssertNoForbiddenNames(await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task TheQueueNeverListsASubmissionTheReviewerSubmittedOrOwns()
    {
        using var host = new AdminReviewApiTestHost(new StubReviewerListingSubmissionReadRepository());
        var visible = Guid.NewGuid();
        host.Stub.Add(Pending(visible, title: "Visible"), Submitter);
        host.Stub.Add(Pending(Guid.NewGuid(), title: "SENTINEL-OWN"), Reviewer);
        host.Stub.Add(Pending(Guid.NewGuid(), title: "SENTINEL-OWNED"), Submitter, linkedOwner: Reviewer);
        using var client = host.CreateReviewerClientFor(Reviewer);

        using var response = await client.GetAsync(Route);

        var body = await response.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.DoesNotContain("SENTINEL", body, StringComparison.Ordinal);
        using var document = JsonDocument.Parse(body);
        Assert.Equal(visible.ToString(), Assert.Single(document.RootElement.GetProperty("items").EnumerateArray()).GetProperty("submissionId").GetString());
        Assert.Equal(1, document.RootElement.GetProperty("totalCount").GetInt64());
    }

    [Fact]
    public async Task AnEmptyQueueIsAnEmptyPage()
    {
        using var host = new AdminReviewApiTestHost(new StubReviewerListingSubmissionReadRepository());
        using var client = host.CreateReviewerClientFor(Reviewer);

        using var response = await client.GetAsync(Route);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(0, document.RootElement.GetProperty("items").GetArrayLength());
        Assert.Equal(0, document.RootElement.GetProperty("totalCount").GetInt64());
        Assert.Equal(ListingSubmissionStatus.PendingReview, host.Stub.LastListCall!.Status);
    }

    [Fact]
    public async Task PagingPassesTheClampedWindowAndReportsTheEnvelope()
    {
        using var host = new AdminReviewApiTestHost(new StubReviewerListingSubmissionReadRepository());
        for (var i = 0; i < 12; i++)
        {
            host.Stub.Add(Pending(Guid.NewGuid(), submittedAt: Submitted.AddMinutes(i)), Submitter);
        }

        using var client = host.CreateReviewerClientFor(Reviewer);

        using var response = await client.GetAsync($"{Route}?page=2&pageSize=5");

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
        using var host = new AdminReviewApiTestHost(new StubReviewerListingSubmissionReadRepository());
        using var client = host.CreateReviewerClientFor(Reviewer);

        using var response = await client.GetAsync($"{Route}?{query}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(expectedSkip, host.Stub.LastListCall!.Skip);
        Assert.Equal(expectedTake, host.Stub.LastListCall.Take);
    }

    [Fact]
    public async Task PurposeAndStatusFiltersReachTheRepositoryAndNarrowTheResult()
    {
        using var host = new AdminReviewApiTestHost(new StubReviewerListingSubmissionReadRepository());
        host.Stub.Add(Pending(Guid.NewGuid(), title: "A sale"), Submitter);
        host.Stub.Add(Pending(Guid.NewGuid(), purpose: ListingPurpose.Rent, title: "A rental"), Submitter);
        host.Stub.Add(Approved(Guid.NewGuid(), Decided, purpose: ListingPurpose.Rent, title: "An approved rental"), Submitter);
        host.Stub.Add(Rejected(Guid.NewGuid(), Decided.AddHours(1), title: "A rejected sale"), Submitter);
        using var client = host.CreateReviewerClientFor(Reviewer);

        using var rentals = await client.GetAsync($"{Route}?purpose=Rent");
        using var rejected = await client.GetAsync($"{Route}?status=Rejected");
        using var approvedRentals = await client.GetAsync($"{Route}?purpose=rent&status=approved");

        Assert.Equal(HttpStatusCode.OK, rentals.StatusCode);
        using var rentalDocument = JsonDocument.Parse(await rentals.Content.ReadAsStringAsync());
        var rental = Assert.Single(rentalDocument.RootElement.GetProperty("items").EnumerateArray());
        Assert.Equal("A rental", rental.GetProperty("title").GetString());
        Assert.Equal("Monthly", rental.GetProperty("price").GetProperty("rentPeriod").GetString());

        Assert.Equal(HttpStatusCode.OK, approvedRentals.StatusCode);
        using var approvedDocument = JsonDocument.Parse(await approvedRentals.Content.ReadAsStringAsync());
        Assert.Equal("An approved rental", Assert.Single(approvedDocument.RootElement.GetProperty("items").EnumerateArray()).GetProperty("title").GetString());
        Assert.Equal(ListingPurpose.Rent, host.Stub.LastListCall!.Purpose);

        using var rejectedDocument = JsonDocument.Parse(await rejected.Content.ReadAsStringAsync());
        Assert.Equal("A rejected sale", Assert.Single(rejectedDocument.RootElement.GetProperty("items").EnumerateArray()).GetProperty("title").GetString());
    }

    [Fact]
    public async Task ASalePriceCarriesNoRentPeriod()
    {
        using var host = new AdminReviewApiTestHost(new StubReviewerListingSubmissionReadRepository());
        host.Stub.Add(Pending(Guid.NewGuid()), Submitter);
        using var client = host.CreateReviewerClientFor(Reviewer);

        using var response = await client.GetAsync(Route);

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var price = Assert.Single(document.RootElement.GetProperty("items").EnumerateArray()).GetProperty("price");
        Assert.False(price.TryGetProperty("rentPeriod", out _));
    }

    [Theory]
    [InlineData("status=Draft")]
    [InlineData("status=draft")]
    public async Task ADraftIsNeverReviewableAndIsA422WithoutReachingTheRepository(string query)
    {
        using var host = new AdminReviewApiTestHost(new StubReviewerListingSubmissionReadRepository());
        using var client = host.CreateReviewerClientFor(Reviewer);

        using var response = await client.GetAsync($"{Route}?{query}");

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(422, document.RootElement.GetProperty("status").GetInt32());
        Assert.Equal(0, host.Stub.ListCallCount);
    }

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
        using var host = new AdminReviewApiTestHost(new StubReviewerListingSubmissionReadRepository());
        using var client = host.CreateReviewerClientFor(Reviewer);

        using var response = await client.GetAsync($"{Route}?{query}");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("request.malformed", document.RootElement.GetProperty("code").GetString());
        Assert.Equal(400, document.RootElement.GetProperty("status").GetInt32());
        Assert.Equal(0, host.Stub.ListCallCount);
    }

    // ----- the detail

    [Fact]
    public async Task APendingDetailIsServedInTheReviewerShapeWithNoDecision()
    {
        var id = Guid.NewGuid();
        using var host = new AdminReviewApiTestHost(new StubReviewerListingSubmissionReadRepository());
        host.Stub.Add(Pending(id), Submitter);
        using var client = host.CreateReviewerClientFor(Reviewer);

        using var response = await client.GetAsync($"{Route}/{id}");

        var body = await response.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;
        Assert.Equal(
            new[]
            {
                "allEvidenceClean", "authorityValidFrom", "createdAt", "description", "evidenceCount", "isCurrentlyPublished",
                "listingId", "location", "ownerKind", "price", "propertyId", "propertyType", "purpose", "representation",
                "status", "submissionId", "submittedAt", "title", "updatedAt", "version",
            },
            SortedNames(root));
        Assert.Equal(id.ToString(), root.GetProperty("submissionId").GetString());
        Assert.Equal("PendingReview", root.GetProperty("status").GetString());
        Assert.Equal("Close to the centre.", root.GetProperty("description").GetString());
        Assert.False(root.GetProperty("isCurrentlyPublished").GetBoolean());
        Assert.Equal(Reviewer, host.Stub.LastGetCall!.ReviewerUserId);
        Assert.Equal(id, host.Stub.LastGetCall.SubmissionId);
        AssertNoForbiddenNames(body);
    }

    [Fact]
    public async Task ARejectedDetailShowsItsReasonAndNothingPrivate()
    {
        var id = Guid.NewGuid();
        using var host = new AdminReviewApiTestHost(new StubReviewerListingSubmissionReadRepository());
        host.Stub.Add(Rejected(id, Decided), Submitter);
        using var client = host.CreateReviewerClientFor(Reviewer);

        using var response = await client.GetAsync($"{Route}/{id}");

        var body = await response.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(body);
        var decision = document.RootElement.GetProperty("decision");
        Assert.Equal(new[] { "approved", "decidedAt", "reason" }, SortedNames(decision));
        Assert.False(decision.GetProperty("approved").GetBoolean());
        Assert.Equal("The deed does not cover this parcel.", decision.GetProperty("reason").GetString());
        AssertNoForbiddenNames(body);
    }

    [Fact]
    public async Task AnApprovedDetailNeverShowsAReasonWhateverIsStored()
    {
        var id = Guid.NewGuid();
        using var host = new AdminReviewApiTestHost(new StubReviewerListingSubmissionReadRepository());
        host.Stub.Add(Approved(id, Decided, storedReason: "SENTINEL-APPROVAL-REASON"), Submitter);
        using var client = host.CreateReviewerClientFor(Reviewer);

        using var response = await client.GetAsync($"{Route}/{id}");

        var body = await response.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.DoesNotContain("SENTINEL", body, StringComparison.Ordinal);
        using var document = JsonDocument.Parse(body);
        var decision = document.RootElement.GetProperty("decision");
        Assert.Equal(new[] { "approved", "decidedAt" }, SortedNames(decision));
        Assert.True(decision.GetProperty("approved").GetBoolean());
    }

    [Fact]
    public async Task AReplacementVersionShowsItsPredecessorAndABoundedAuthorityShowsItsEnd()
    {
        var id = Guid.NewGuid();
        var predecessor = Guid.NewGuid();
        using var host = new AdminReviewApiTestHost(new StubReviewerListingSubmissionReadRepository());
        host.Stub.Add(
            Pending(id) with { Version = 2, PredecessorSubmissionId = predecessor, AuthorityValidUntil = ValidFrom.AddYears(1) },
            Submitter);
        using var client = host.CreateReviewerClientFor(Reviewer);

        using var response = await client.GetAsync($"{Route}/{id}");

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(predecessor.ToString(), document.RootElement.GetProperty("predecessorSubmissionId").GetString());
        Assert.True(document.RootElement.TryGetProperty("authorityValidUntil", out _));
        Assert.Equal(2, document.RootElement.GetProperty("version").GetInt32());
    }

    [Fact]
    public async Task AMissingSubmissionIsA404WithTheStandardBody()
    {
        using var host = new AdminReviewApiTestHost(new StubReviewerListingSubmissionReadRepository());
        host.Stub.Add(Pending(Guid.NewGuid(), title: "SENTINEL-OTHER"), Submitter);
        using var client = host.CreateReviewerClientFor(Reviewer);
        var missing = Guid.NewGuid();

        using var response = await client.GetAsync($"{Route}/{missing}");

        var body = await response.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        using var document = JsonDocument.Parse(body);
        Assert.Equal("listing_submissions.not_found", document.RootElement.GetProperty("code").GetString());
        Assert.Equal(404, document.RootElement.GetProperty("status").GetInt32());
        Assert.DoesNotContain("SENTINEL", body, StringComparison.Ordinal);
        Assert.DoesNotContain(missing.ToString(), body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ADraftIsNeverReviewableAndIsTheSame404AsAMissingSubmission()
    {
        var draft = Guid.NewGuid();
        using var host = new AdminReviewApiTestHost(new StubReviewerListingSubmissionReadRepository());
        host.Stub.Add(Pending(draft, title: "SENTINEL-DRAFT") with { Status = ListingSubmissionStatus.Draft }, Submitter);
        using var client = host.CreateReviewerClientFor(Reviewer);

        using var response = await client.GetAsync($"{Route}/{draft}");

        var body = await response.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.DoesNotContain("SENTINEL", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheEmptyGuidIsA404WithoutAskingTheRepository()
    {
        using var host = new AdminReviewApiTestHost(new StubReviewerListingSubmissionReadRepository());
        using var client = host.CreateReviewerClientFor(Reviewer);

        using var response = await client.GetAsync($"{Route}/{Guid.Empty}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(0, host.Stub.GetCallCount);
    }

    [Fact]
    public async Task ANonGuidIdMatchesNoRouteAndNeverReachesTheRepository()
    {
        using var host = new AdminReviewApiTestHost(new StubReviewerListingSubmissionReadRepository());
        using var client = host.CreateReviewerClientFor(Reviewer);

        using var response = await client.GetAsync($"{Route}/not-a-guid");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(0, host.Stub.GetCallCount);
    }

    // ----- independence

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AReviewerWhoSubmittedOrOwnsTheSubmissionIsA403NotIndependentAndSeesNothingOfIt(bool isSubmitter)
    {
        var id = Guid.NewGuid();
        using var host = new AdminReviewApiTestHost(new StubReviewerListingSubmissionReadRepository());
        host.Stub.Add(
            Pending(id, title: "SENTINEL-TITLE"),
            isSubmitter ? Reviewer : Submitter,
            linkedOwner: isSubmitter ? null : Reviewer);
        using var client = host.CreateReviewerClientFor(Reviewer);

        using var response = await client.GetAsync($"{Route}/{id}");

        var body = await response.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        using var document = JsonDocument.Parse(body);
        Assert.Equal("listing_submissions.reviewer_not_independent", document.RootElement.GetProperty("code").GetString());
        Assert.Equal(403, document.RootElement.GetProperty("status").GetInt32());
        Assert.DoesNotContain("SENTINEL", body, StringComparison.Ordinal);
        Assert.DoesNotContain(id.ToString(), body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AReviewerWhoseIndependenceCannotBeEstablishedIsRefusedAndSeesNothing()
    {
        var id = Guid.NewGuid();
        using var host = new AdminReviewApiTestHost(new StubReviewerListingSubmissionReadRepository());
        host.Stub.Add(Pending(id, title: "SENTINEL-TITLE"), Submitter, unverifiable: true);
        using var client = host.CreateReviewerClientFor(Reviewer);

        using var response = await client.GetAsync($"{Route}/{id}");

        var body = await response.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        using var document = JsonDocument.Parse(body);
        Assert.Equal("listing_submissions.independence_unverifiable", document.RootElement.GetProperty("code").GetString());
        Assert.DoesNotContain("SENTINEL", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnotherReviewerWhoIsIndependentStillSeesTheSubmission()
    {
        var id = Guid.NewGuid();
        var other = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
        using var host = new AdminReviewApiTestHost(new StubReviewerListingSubmissionReadRepository());
        host.Stub.Add(Pending(id), Reviewer);
        using var client = host.CreateReviewerClientFor(other);

        using var response = await client.GetAsync($"{Route}/{id}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(other, host.Stub.LastGetCall!.ReviewerUserId);
    }

    // ----- failures

    [Fact]
    public async Task ARepositoryFailureIsAGeneric500ThatLeaksNothing()
    {
        using var host = new AdminReviewApiTestHost(new StubReviewerListingSubmissionReadRepository
        {
            ExceptionToThrow = new InvalidOperationException("SENTINEL-DATABASE-DETAIL"),
        });
        using var client = host.CreateReviewerClientFor(Reviewer);

        using var list = await client.GetAsync(Route);
        using var detail = await client.GetAsync($"{Route}/{Guid.NewGuid()}");

        await AssertGenericServerErrorAsync(list);
        await AssertGenericServerErrorAsync(detail);
    }

    [Fact]
    public async Task ACorruptRowIsAGeneric500ThatLeaksNothingAndIsNeverSkipped()
    {
        var corrupt = Guid.NewGuid();
        using var host = new AdminReviewApiTestHost(new StubReviewerListingSubmissionReadRepository());
        host.Stub.Add(Pending(Guid.NewGuid(), submittedAt: Submitted), Submitter);
        host.Stub.Add(Pending(corrupt, title: "SENTINEL-TITLE", submittedAt: Submitted.AddHours(1)) with { Version = 0 }, Submitter);
        using var client = host.CreateReviewerClientFor(Reviewer);

        using var list = await client.GetAsync(Route);
        using var detail = await client.GetAsync($"{Route}/{corrupt}");

        await AssertGenericServerErrorAsync(list);
        await AssertGenericServerErrorAsync(detail);
    }

    [Fact]
    public async Task ALookupForADifferentSubmissionThanAskedIsAGeneric500NeverServed()
    {
        var asked = Guid.NewGuid();
        using var host = new AdminReviewApiTestHost(new StubReviewerListingSubmissionReadRepository());
        host.Stub.Force(asked, ReviewDetailLookup.Found(Pending(Guid.NewGuid(), title: "SENTINEL-OTHER")));
        using var client = host.CreateReviewerClientFor(Reviewer);

        using var response = await client.GetAsync($"{Route}/{asked}");

        await AssertGenericServerErrorAsync(response);
    }

    // ----- routing and production reachability

    [Fact]
    public async Task OnlyGetIsAllowedOnBothRoutes()
    {
        var id = Guid.NewGuid();
        using var host = new AdminReviewApiTestHost(new StubReviewerListingSubmissionReadRepository());
        host.Stub.Add(Pending(id), Submitter);
        using var client = host.CreateReviewerClientFor(Reviewer);

        foreach (var method in new[] { HttpMethod.Post, HttpMethod.Put, HttpMethod.Delete, HttpMethod.Patch })
        {
            using var list = await client.SendAsync(new HttpRequestMessage(method, Route));
            using var detail = await client.SendAsync(new HttpRequestMessage(method, $"{Route}/{id}"));

            Assert.Equal(HttpStatusCode.MethodNotAllowed, list.StatusCode);
            Assert.Equal(HttpStatusCode.MethodNotAllowed, detail.StatusCode);
        }
    }

    [Fact]
    public void TheMountedHostExposesExactlyTheTwoGetRoutesUnderTheAdminMarketplacePath()
    {
        using var host = new AdminReviewApiTestHost(new StubReviewerListingSubmissionReadRepository());

        var endpoints = host.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>()
            .Where(endpoint => (endpoint.RoutePattern.RawText ?? string.Empty).StartsWith("api/admin/marketplace", StringComparison.OrdinalIgnoreCase))
            .ToArray();

        Assert.Equal(
            new[] { "api/admin/marketplace/listing-submissions", "api/admin/marketplace/listing-submissions/{submissionId:guid}" },
            endpoints.Select(endpoint => endpoint.RoutePattern.RawText!).Order(StringComparer.Ordinal).ToArray());
        Assert.All(
            endpoints,
            endpoint => Assert.Equal(
                new[] { "GET" },
                endpoint.Metadata.GetMetadata<HttpMethodMetadata>()!.HttpMethods.ToArray()));
    }

    [Fact]
    public void TheControllerRequiresAuthenticationAndExactlyTheReviewReadPermissionAndIsNotPublic()
    {
        var type = typeof(AdminListingSubmissionReviewController);

        Assert.False(type.IsVisible);
        var requirements = type.GetCustomAttributes(typeof(RequirePermissionAttribute), inherit: true)
            .Cast<RequirePermissionAttribute>()
            .ToArray();
        Assert.Equal(PermissionCodes.MarketplaceSubmissionsReviewRead, Assert.Single(requirements).PermissionCode);
        Assert.NotEmpty(type.GetCustomAttributes(typeof(IAuthorizeData), inherit: true));
        Assert.Empty(type.GetCustomAttributes(typeof(AllowAnonymousAttribute), inherit: true));

        foreach (var method in type.GetMethods().Where(method => method.DeclaringType == type))
        {
            Assert.Empty(method.GetCustomAttributes(typeof(AllowAnonymousAttribute), inherit: true));
            Assert.Empty(method.GetCustomAttributes(typeof(RequirePermissionAttribute), inherit: true));
        }
    }

    [Fact]
    public async Task TheProductionCompositionHasNoReviewRouteAndNoReviewServiceAtAll()
    {
        using var host = new AdminReviewApiTestHost(new StubReviewerListingSubmissionReadRepository(), mount: false);
        host.Permissions.AllowedPermissionCodes.Add(PermissionCodes.MarketplaceSubmissionsReviewRead);
        using var client = host.CreateClientFor(Reviewer);

        var routes = host.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>()
            .Select(endpoint => endpoint.RoutePattern.RawText ?? string.Empty)
            .ToArray();
        using var list = await client.GetAsync(Route);
        using var detail = await client.GetAsync($"{Route}/{Guid.NewGuid()}");

        // The inventory is real: existing routes are visible to it.
        Assert.Contains(routes, route => route.StartsWith("api/public/marketplace/listings", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(routes, route => route.Contains("admin/marketplace", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(HttpStatusCode.NotFound, list.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, detail.StatusCode);
        Assert.Equal(0, host.Stub.ListCallCount);
        Assert.Equal(0, host.Stub.GetCallCount);
        Assert.Equal(0, host.Permissions.CallCount);

        Assert.Null(host.Services.GetService<IQueryHandler<GetReviewQueueQuery, PagedList<ReviewQueueItem>>>());
        Assert.Null(host.Services.GetService<IQueryHandler<GetReviewSubmissionQuery, ReviewSubmissionDetail>>());
        Assert.Null(host.Services.GetService<IReviewReadAuthorizer>());
        Assert.Null(host.Services.GetService<IReviewerListingSubmissionReadRepository>());
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

    private static async Task AssertForbiddenAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        using var document = JsonDocument.Parse(body);
        Assert.Equal("authorization.forbidden", document.RootElement.GetProperty("code").GetString());
        Assert.Equal(403, document.RootElement.GetProperty("status").GetInt32());

        // The refusal names no permission, role or submission.
        Assert.DoesNotContain("marketplace.", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("SENTINEL", body, StringComparison.Ordinal);
    }

    private static async Task AssertGenericServerErrorAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        using var document = JsonDocument.Parse(body);
        Assert.Equal("server.unexpected_error", document.RootElement.GetProperty("code").GetString());
        Assert.DoesNotContain("SENTINEL", body, StringComparison.Ordinal);
    }

    // Checks the JSON property names, not the values.
    private static void AssertNoForbiddenNames(string json)
    {
        using var document = JsonDocument.Parse(json);
        var names = new List<string>();
        CollectNames(document.RootElement, names);

        Assert.NotEmpty(names);

        foreach (var name in names)
        {
            foreach (var term in ForbiddenNameTerms)
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

    private static ReviewSubmissionDetail Pending(
        Guid id,
        ListingPurpose purpose = ListingPurpose.Sale,
        string title = "Two bedroom apartment",
        DateTimeOffset? submittedAt = null)
    {
        var submitted = submittedAt ?? Submitted;

        return new ReviewSubmissionDetail(
            id,
            Guid.NewGuid(),
            Guid.NewGuid(),
            null,
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
            ListingOwnerKind.Individual,
            ValidFrom,
            null,
            Created,
            submitted,
            submitted,
            2,
            true,
            false,
            null);
    }

    private static ReviewSubmissionDetail Approved(
        Guid id,
        DateTimeOffset updatedAt,
        ListingPurpose purpose = ListingPurpose.Sale,
        string title = "Two bedroom apartment",
        string? storedReason = null)
    {
        return Pending(id, purpose, title) with
        {
            Status = ListingSubmissionStatus.Approved,
            UpdatedAt = updatedAt,
            Decision = new ReviewDecisionSummary(Approved: true, storedReason, Decided),
        };
    }

    private static ReviewSubmissionDetail Rejected(Guid id, DateTimeOffset updatedAt, string title = "Two bedroom apartment")
    {
        return Pending(id, ListingPurpose.Sale, title) with
        {
            Status = ListingSubmissionStatus.Rejected,
            UpdatedAt = updatedAt,
            Decision = new ReviewDecisionSummary(Approved: false, "The deed does not cover this parcel.", Decided),
        };
    }
}
