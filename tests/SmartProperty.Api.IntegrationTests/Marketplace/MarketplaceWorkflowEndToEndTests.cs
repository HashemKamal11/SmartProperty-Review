using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using SmartProperty.Api.IntegrationTests.Infrastructure;
using SmartProperty.Application.Abstractions.Marketplace;
using SmartProperty.Application.Authorization;
using SmartProperty.Domain.Marketplace;
using SmartProperty.Domain.PropertyRegistry;
using Xunit;

namespace SmartProperty.Api.IntegrationTests.Marketplace;

/// <summary>
/// The marketplace workflow end to end over HTTP: real pipeline, real JWT authentication, the persisted permission
/// checker, the real handlers and repositories, and real PostgreSQL rows.
/// </summary>
/// <remarks>
/// <para>
/// Grants are seeded explicitly through the Domain (nothing in the product issues them). The two trusted sources that
/// production registers as ones that never answer, the evidence provider and the reviewer-independence provider, are
/// replaced with test fakes in the host that needs them; the tests that prove the fail-closed behaviour use the
/// production composition untouched.
/// </para>
/// <para>
/// This proves the HTTP surface and the workflow behind it. It does not prove production readiness: grant issuance,
/// evidence verification and reviewer independence have no real implementation yet.
/// </para>
/// </remarks>
[Collection(ApiPostgreSqlCollection.Name)]
public sealed class MarketplaceWorkflowEndToEndTests : IDisposable
{
    private static readonly DateOnly June1 = new(2027, 6, 1);

    private readonly ApiPostgreSqlFixture _fixture;
    private readonly SmartPropertyApiFactory _production;
    private readonly SmartPropertyApiFactory _fakedRoot;
    private readonly WebApplicationFactory<Program> _faked;
    private readonly AuthScenario _scenario;

    public MarketplaceWorkflowEndToEndTests(ApiPostgreSqlFixture fixture)
    {
        _fixture = fixture;
        _production = new SmartPropertyApiFactory(fixture, usePersistencePermissionChecker: true);
        _fakedRoot = new SmartPropertyApiFactory(fixture, usePersistencePermissionChecker: true);
        _faked = _fakedRoot.WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services =>
            {
                services.AddScoped<IEvidenceAssessmentProvider, AnsweringEvidence>();
                services.AddScoped<IReviewerIndependenceProvider, IndependentReviewer>();
            }));
        _scenario = new AuthScenario(fixture);
    }

    public void Dispose()
    {
        _production.Dispose();
        _faked.Dispose();
        _fakedRoot.Dispose();
    }

    // ---- who may call

    [Theory]
    [InlineData("POST", "/api/marketplace/me/listing-submissions")]
    [InlineData("PUT", "/api/marketplace/me/listing-submissions/22222222-2222-2222-2222-222222222222")]
    [InlineData("DELETE", "/api/marketplace/me/listing-submissions/22222222-2222-2222-2222-222222222222?expectedRevision=1")]
    [InlineData("POST", "/api/marketplace/me/listing-submissions/22222222-2222-2222-2222-222222222222/submit")]
    [InlineData("POST", "/api/marketplace/me/properties/22222222-2222-2222-2222-222222222222/listing-drafts")]
    [InlineData("POST", "/api/marketplace/me/listings/22222222-2222-2222-2222-222222222222/withdraw")]
    [InlineData("POST", "/api/marketplace/purchase-requests")]
    [InlineData("POST", "/api/marketplace/rental-requests")]
    [InlineData("GET", "/api/marketplace/me/requests")]
    [InlineData("GET", "/api/marketplace/owner/requests")]
    [InlineData("POST", "/api/marketplace/owner/rental-requests/22222222-2222-2222-2222-222222222222/accept")]
    [InlineData("GET", "/api/marketplace/me/commitments")]
    [InlineData("POST", "/api/marketplace/me/commitments/22222222-2222-2222-2222-222222222222/confirm-signed")]
    [InlineData("POST", "/api/admin/marketplace/listing-submissions/22222222-2222-2222-2222-222222222222/approve")]
    [InlineData("PUT", "/api/admin/marketplace/settings/signing-window")]
    public async Task EveryNewRouteRefusesAnAnonymousCaller(string method, string route)
    {
        using var client = _production.CreateClient();
        using var request = new HttpRequestMessage(new HttpMethod(method), route)
        {
            Content = method is "GET" or "DELETE" ? null : JsonContent.Create(new { }),
        };

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task OnlyAnAdministratorWithTheDecidePermissionReachesADecision()
    {
        var user = await ActiveUserAsync();
        using var client = Authenticated(_production, user);

        using var response = await client.PostAsJsonAsync(
            $"/api/admin/marketplace/listing-submissions/{Guid.NewGuid()}/reject", new { reason = "x" });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    // ---- fail closed in the production composition

    [Fact]
    public async Task InProductionADraftWithoutEvidenceIsSavedButUnknownEvidenceAndSubmissionAreRefusedClearly()
    {
        var world = await SeedAsync(grants: [ListingPurpose.Sale]);
        using var client = Authenticated(_production, world.Owner);

        using var draft = await client.PostAsJsonAsync("/api/marketplace/me/listing-submissions", SaleDraft(world, evidence: []));
        using var withEvidence = await client.PostAsJsonAsync("/api/marketplace/me/listing-submissions", SaleDraft(world, evidence: [Guid.NewGuid()]));
        var body = await ReadAsync(draft);
        using var submit = await client.PostAsJsonAsync(
            $"/api/marketplace/me/listing-submissions/{body.GetProperty("submissionId").GetGuid()}/submit",
            new { expectedRevision = body.GetProperty("revision").GetInt32() });

        Assert.Equal(HttpStatusCode.Created, draft.StatusCode);
        // Production evidence is the owner's own scanned authority-request documents; an id that is not one of them is
        // simply not available, so nothing is attached and nothing is claimed.
        Assert.Equal(HttpStatusCode.UnprocessableEntity, withEvidence.StatusCode);
        Assert.Equal("validation.failed", (await ReadAsync(withEvidence)).GetProperty("code").GetString());
        // A draft with no evidence cannot be submitted: nothing was verified, and nothing is claimed clean.
        Assert.Equal(HttpStatusCode.Conflict, submit.StatusCode);
        Assert.Equal("listing_submissions.prerequisites_unverified", (await ReadAsync(submit)).GetProperty("code").GetString());
    }

    [Fact]
    public async Task InProductionAnApprovalIsRefusedBecauseNoReviewerIndependenceCanBeEstablished()
    {
        var world = await SeedAsync(grants: [ListingPurpose.Sale]);
        var reviewer = await AdministratorAsync();
        var pending = await SubmittedAsync(_faked, world, ListingPurpose.Sale);
        using var client = Authenticated(_production, reviewer);

        using var response = await client.PostAsync($"/api/admin/marketplace/listing-submissions/{pending}/approve", null);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("listing_submissions.independence_unverifiable", (await ReadAsync(response)).GetProperty("code").GetString());
    }

    // ---- the advertiser

    [Fact]
    public async Task AdvertisingForBothCreatesTwoDraftsAndNeedsBothGrants()
    {
        var both = await SeedAsync(grants: [ListingPurpose.Sale, ListingPurpose.Rent]);
        var onlySale = await SeedAsync(grants: [ListingPurpose.Sale]);
        using var ok = await Authenticated(_faked, both.Owner).PostAsJsonAsync(
            $"/api/marketplace/me/properties/{both.PropertyId}/listing-drafts", new { sale = SaleContent([]), rent = RentContent([]) });
        using var refused = await Authenticated(_faked, onlySale.Owner).PostAsJsonAsync(
            $"/api/marketplace/me/properties/{onlySale.PropertyId}/listing-drafts", new { sale = SaleContent([]), rent = RentContent([]) });
        using var neither = await Authenticated(_faked, both.Owner).PostAsJsonAsync(
            $"/api/marketplace/me/properties/{both.PropertyId}/listing-drafts", new { });

        var created = await ReadAsync(ok);
        Assert.Equal(HttpStatusCode.Created, ok.StatusCode);
        Assert.NotEqual(created.GetProperty("sale").GetProperty("listingId").GetGuid(), created.GetProperty("rent").GetProperty("listingId").GetGuid());
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.Equal("listing_submissions.not_eligible", (await ReadAsync(refused)).GetProperty("code").GetString());
        Assert.Equal(HttpStatusCode.UnprocessableEntity, neither.StatusCode);
    }

    [Fact]
    public async Task AnUnknownPurposeOrNumericEnumIsRejectedAsAValidationFailure()
    {
        var world = await SeedAsync(grants: [ListingPurpose.Sale]);
        using var client = Authenticated(_faked, world.Owner);

        using var unknown = await client.PostAsJsonAsync("/api/marketplace/me/listing-submissions",
            new { propertyId = world.PropertyId, purpose = "Both", content = SaleContent([]) });
        using var numeric = await client.PostAsJsonAsync("/api/marketplace/me/listing-submissions",
            new { propertyId = world.PropertyId, purpose = "1", content = SaleContent([]) });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, unknown.StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, numeric.StatusCode);
    }

    // ---- the whole path: sale

    [Fact]
    public async Task ASaleGoesFromDraftToPublicationToAcceptedCommitmentOverHttp()
    {
        var world = await SeedAsync(grants: [ListingPurpose.Sale]);
        var reviewer = await AdministratorAsync();
        var buyer = await ActiveUserAsync();
        var submissionId = await SubmittedAsync(_faked, world, ListingPurpose.Sale);

        using var approve = await Authenticated(_faked, reviewer).PostAsync($"/api/admin/marketplace/listing-submissions/{submissionId}/approve", null);
        var approved = await ReadAsync(approve);
        var listingId = approved.GetProperty("listingId").GetGuid();
        using var publicDetail = await _faked.CreateClient().GetAsync($"/api/public/marketplace/listings/{listingId}");

        Assert.Equal(HttpStatusCode.OK, approve.StatusCode);
        Assert.Equal("Approved", approved.GetProperty("status").GetString());
        Assert.Equal(HttpStatusCode.OK, publicDetail.StatusCode);

        using var request = await Authenticated(_faked, buyer).PostAsJsonAsync(
            "/api/marketplace/purchase-requests", new { listingId, offeredAmount = 4_900_000m, message = "Interested" });
        var requestId = (await ReadAsync(request)).GetProperty("requestId").GetGuid();
        Assert.Equal(HttpStatusCode.Created, request.StatusCode);

        // The advertiser sees who asked; the buyer sees only their own request, and a stranger sees neither.
        using var received = await Authenticated(_faked, world.Owner).GetAsync("/api/marketplace/owner/requests");
        using var mine = await Authenticated(_faked, buyer).GetAsync($"/api/marketplace/me/purchase-requests/{requestId}");
        using var strangerRead = await Authenticated(_faked, await ActiveUserAsync()).GetAsync($"/api/marketplace/me/purchase-requests/{requestId}");
        Assert.Equal(1, (await ReadAsync(received)).GetProperty("items").GetArrayLength());
        Assert.False(string.IsNullOrWhiteSpace((await ReadAsync(received)).GetProperty("items")[0].GetProperty("requesterName").GetString()));
        Assert.Equal(HttpStatusCode.OK, mine.StatusCode);
        Assert.False((await ReadAsync(mine)).TryGetProperty("requesterName", out _));
        Assert.Equal(HttpStatusCode.NotFound, strangerRead.StatusCode);

        using var accept = await Authenticated(_faked, world.Owner).PostAsync($"/api/marketplace/owner/purchase-requests/{requestId}/accept", null);
        var accepted = await ReadAsync(accept);
        var commitmentId = accepted.GetProperty("commitmentId").GetGuid();
        Assert.Equal(HttpStatusCode.OK, accept.StatusCode);

        // Accepting closes the advertisement: it leaves the public marketplace.
        using var afterAccept = await _faked.CreateClient().GetAsync($"/api/public/marketplace/listings/{listingId}");
        Assert.Equal(HttpStatusCode.NotFound, afterAccept.StatusCode);

        // The owner records the steps; the customer can read the commitment but not take its steps.
        using var customerStep = await Authenticated(_faked, buyer).PostAsync($"/api/marketplace/me/commitments/{commitmentId}/confirm-signed", null);
        using var signed = await Authenticated(_faked, world.Owner).PostAsync($"/api/marketplace/me/commitments/{commitmentId}/confirm-signed", null);
        using var handover = await Authenticated(_faked, world.Owner).PostAsync($"/api/marketplace/me/commitments/{commitmentId}/confirm-handover", null);
        using var asCustomer = await Authenticated(_faked, buyer).GetAsync($"/api/marketplace/me/commitments/{commitmentId}");
        Assert.Equal(HttpStatusCode.NotFound, customerStep.StatusCode);
        Assert.Equal(HttpStatusCode.OK, signed.StatusCode);
        Assert.Equal("Completed", (await ReadAsync(handover)).GetProperty("status").GetString());
        Assert.Equal("Completed", (await ReadAsync(asCustomer)).GetProperty("status").GetString());
    }

    // ---- the whole path: dated rental

    [Fact]
    public async Task ADatedRentalIsRequestedAcceptedAndItsDatesAreProtectedOverHttp()
    {
        var world = await SeedAsync(grants: [ListingPurpose.Rent]);
        var reviewer = await AdministratorAsync();
        var tenant = await ActiveUserAsync();
        var submissionId = await SubmittedAsync(_faked, world, ListingPurpose.Rent);
        using var approve = await Authenticated(_faked, reviewer).PostAsync($"/api/admin/marketplace/listing-submissions/{submissionId}/approve", null);
        var listingId = (await ReadAsync(approve)).GetProperty("listingId").GetGuid();

        using var request = await Authenticated(_faked, tenant).PostAsJsonAsync(
            "/api/marketplace/rental-requests",
            new { listingId, pricingPeriod = "Monthly", startDate = "2027-06-01", endDate = "2027-08-01", message = "Two months" });
        var created = await ReadAsync(request);
        Assert.Equal(HttpStatusCode.Created, request.StatusCode);

        using var detail = await Authenticated(_faked, tenant).GetAsync($"/api/marketplace/me/rental-requests/{created.GetProperty("requestId").GetGuid()}");
        var view = await ReadAsync(detail);
        Assert.Equal(20_000m, view.GetProperty("amount").GetDecimal());
        Assert.Equal("EGP", view.GetProperty("currency").GetString());
        Assert.Equal("Monthly", view.GetProperty("pricingPeriod").GetString());
        Assert.Equal("2027-06-01", view.GetProperty("startDate").GetString());
        Assert.Equal("2027-08-01", view.GetProperty("endDate").GetString());
        Assert.Equal("14:00:00", view.GetProperty("checkInTime").GetString());
        Assert.Equal("Africa/Cairo", view.GetProperty("timeZoneId").GetString());

        // Dates outside the availability window and a period the listing does not offer are refused.
        using var outside = await Authenticated(_faked, await ActiveUserAsync()).PostAsJsonAsync(
            "/api/marketplace/rental-requests", new { listingId, pricingPeriod = "Monthly", startDate = "2027-10-01", endDate = "2027-11-01" });
        using var yearly = await Authenticated(_faked, await ActiveUserAsync()).PostAsJsonAsync(
            "/api/marketplace/rental-requests", new { listingId, pricingPeriod = "Yearly", startDate = "2027-06-01", endDate = "2027-07-01" });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, outside.StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, yearly.StatusCode);

        using var accept = await Authenticated(_faked, world.Owner).PostAsync(
            $"/api/marketplace/owner/rental-requests/{created.GetProperty("requestId").GetGuid()}/accept", null);
        var accepted = await ReadAsync(accept);
        Assert.Equal(HttpStatusCode.OK, accept.StatusCode);
        Assert.True((accepted.GetProperty("signingDeadline").GetDateTimeOffset() - accepted.GetProperty("acceptedAt").GetDateTimeOffset()).TotalHours is > 71.9 and < 72.1);

        using var commitments = await Authenticated(_faked, tenant).GetAsync("/api/marketplace/me/commitments?role=Customer");
        var items = (await ReadAsync(commitments)).GetProperty("items");
        Assert.Equal(1, items.GetArrayLength());
        Assert.Equal("Rental", items[0].GetProperty("kind").GetString());
        Assert.Equal("PendingSignature", items[0].GetProperty("status").GetString());
    }

    // ---- the signing window

    [Fact]
    public async Task OnlyAnAdministratorWithTheSettingsPermissionChangesTheSigningWindow()
    {
        var plain = await ActiveUserAsync();
        var admin = await ActiveUserAsync();
        await _scenario.GrantPlatformPermissionAsync(admin, PermissionCodes.MarketplaceSettingsManage);

        using var forbidden = await Authenticated(_production, plain).PutAsJsonAsync("/api/admin/marketplace/settings/signing-window", new { hours = 24 });
        using var ok = await Authenticated(_production, admin).PutAsJsonAsync("/api/admin/marketplace/settings/signing-window", new { hours = 24 });
        using var tooLong = await Authenticated(_production, admin).PutAsJsonAsync("/api/admin/marketplace/settings/signing-window", new { hours = 9999 });
        using var missing = await Authenticated(_production, admin).PutAsJsonAsync("/api/admin/marketplace/settings/signing-window", new { });
        using var read = await Authenticated(_production, admin).GetAsync("/api/admin/marketplace/settings/signing-window");

        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, tooLong.StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, missing.StatusCode);
        Assert.Equal(24, (await ReadAsync(read)).GetProperty("hours").GetInt32());
        Assert.False((await ReadAsync(read)).GetProperty("isDefault").GetBoolean());
    }

    // ---- the AI search index follows the marketplace, by listing id

    [Fact]
    public async Task PublishingIndexesTheAdvertisementUnderItsListingIdAndWithdrawingRemovesIt()
    {
        var world = await SeedAsync(grants: [ListingPurpose.Sale]);
        var reviewer = await AdministratorAsync();
        var submissionId = await SubmittedAsync(_faked, world, ListingPurpose.Sale);

        using var approve = await Authenticated(_faked, reviewer).PostAsync($"/api/admin/marketplace/listing-submissions/{submissionId}/approve", null);
        var listingId = (await ReadAsync(approve)).GetProperty("listingId").GetGuid();

        var put = Assert.Single(_fakedRoot.AiGateway.Puts, entry => entry.ExternalId == listingId.ToString("D"));
        Assert.Equal("Sale", put.Listing.TransactionType);
        Assert.Equal(5_000_000m, put.Listing.Price);
        Assert.Equal(150m, put.Listing.AreaSqm);
        Assert.Equal(3, put.Listing.Bedrooms);
        Assert.Equal("Villa for sale", put.Listing.TitleEn);
        Assert.Equal("Available", put.Listing.ListingStatus);

        using var withdraw = await Authenticated(_faked, world.Owner).PostAsync($"/api/marketplace/me/listings/{listingId}/withdraw", null);
        Assert.Equal(HttpStatusCode.OK, withdraw.StatusCode);
        Assert.Contains(listingId.ToString("D"), _fakedRoot.AiGateway.Deletes);
    }

    [Fact]
    public async Task AcceptingARequestRemovesTheClosedAdvertisementsFromTheIndex()
    {
        var world = await SeedAsync(grants: [ListingPurpose.Rent]);
        var reviewer = await AdministratorAsync();
        var tenant = await ActiveUserAsync();
        var submissionId = await SubmittedAsync(_faked, world, ListingPurpose.Rent);
        using var approve = await Authenticated(_faked, reviewer).PostAsync($"/api/admin/marketplace/listing-submissions/{submissionId}/approve", null);
        var listingId = (await ReadAsync(approve)).GetProperty("listingId").GetGuid();
        using var request = await Authenticated(_faked, tenant).PostAsJsonAsync(
            "/api/marketplace/rental-requests", new { listingId, pricingPeriod = "Monthly", startDate = "2027-06-01", endDate = "2027-07-01" });
        var requestId = (await ReadAsync(request)).GetProperty("requestId").GetGuid();

        using var accept = await Authenticated(_faked, world.Owner).PostAsync($"/api/marketplace/owner/rental-requests/{requestId}/accept", null);

        Assert.Equal(HttpStatusCode.OK, accept.StatusCode);
        Assert.Contains(listingId.ToString("D"), _fakedRoot.AiGateway.Deletes);
    }

    [Fact]
    public async Task AnUnavailableAiServiceNeverChangesACommittedDecision()
    {
        var world = await SeedAsync(grants: [ListingPurpose.Sale]);
        var reviewer = await AdministratorAsync();
        var submissionId = await SubmittedAsync(_faked, world, ListingPurpose.Sale);
        _fakedRoot.AiGateway.PutError = new SmartProperty.Common.Results.Error("ai.down", "down", SmartProperty.Common.Results.ErrorType.ServiceUnavailable);

        using var approve = await Authenticated(_faked, reviewer).PostAsync($"/api/admin/marketplace/listing-submissions/{submissionId}/approve", null);
        var listingId = (await ReadAsync(approve)).GetProperty("listingId").GetGuid();
        using var publicDetail = await _faked.CreateClient().GetAsync($"/api/public/marketplace/listings/{listingId}");

        _fakedRoot.AiGateway.PutError = null;
        Assert.Equal(HttpStatusCode.OK, approve.StatusCode);
        Assert.Equal(HttpStatusCode.OK, publicDetail.StatusCode);
    }

    [Fact]
    public async Task AnAiResultMapsToThePropertyOnlyThroughAVisibleListingId()
    {
        var world = await SeedAsync(grants: [ListingPurpose.Sale]);
        var reviewer = await AdministratorAsync();
        var searcher = await ActiveUserAsync();
        var submissionId = await SubmittedAsync(_faked, world, ListingPurpose.Sale);
        using var approve = await Authenticated(_faked, reviewer).PostAsync($"/api/admin/marketplace/listing-submissions/{submissionId}/approve", null);
        var listingId = (await ReadAsync(approve)).GetProperty("listingId").GetGuid();
        _fakedRoot.AiGateway.SearchOutcome = new SmartProperty.Application.Abstractions.Ai.AiSearchOutcome("q", "q", 3,
        [
            AiHit(listingId.ToString("D"), "PF-1"),
            AiHit(world.PropertyId.ToString("D"), "PF-2"),
            AiHit(Guid.NewGuid().ToString("D"), "PF-3"),
        ]);

        using var search = await Authenticated(_faked, searcher).PostAsJsonAsync("/api/property-search", new { query = "villa" });
        var items = (await ReadAsync(search)).GetProperty("items");

        Assert.Equal(HttpStatusCode.OK, search.StatusCode);
        Assert.Equal(world.PropertyId, items[0].GetProperty("propertyId").GetGuid());
        Assert.Equal(listingId, items[0].GetProperty("listing").GetProperty("listingId").GetGuid());
        // An older record whose external id is the property id, and an unknown id, map to nothing.
        Assert.Equal(JsonValueKind.Null, items[1].GetProperty("propertyId").ValueKind);
        Assert.False(items[1].TryGetProperty("listing", out var stale) && stale.ValueKind != JsonValueKind.Null);
        Assert.Equal(JsonValueKind.Null, items[2].GetProperty("propertyId").ValueKind);
    }

    // ---- favorites and comparison carry the visible advertisements

    [Fact]
    public async Task FavoritesAndComparisonShowThePriceAndTermsFromTheCurrentListings()
    {
        var world = await SeedAsync(grants: [ListingPurpose.Sale, ListingPurpose.Rent]);
        var reviewer = await AdministratorAsync();
        var fan = await ActiveUserAsync();
        var sale = await SubmittedAsync(_faked, world, ListingPurpose.Sale);
        var rent = await SubmittedAsync(_faked, world, ListingPurpose.Rent);
        using var approveSale = await Authenticated(_faked, reviewer).PostAsync($"/api/admin/marketplace/listing-submissions/{sale}/approve", null);
        using var approveRent = await Authenticated(_faked, reviewer).PostAsync($"/api/admin/marketplace/listing-submissions/{rent}/approve", null);
        using var mark = await Authenticated(_faked, fan).PutAsync($"/api/favorites/{world.PropertyId}", null);
        using var compare = await Authenticated(_faked, fan).PutAsync($"/api/comparison/{world.PropertyId}", null);

        using var favorites = await Authenticated(_faked, fan).GetAsync("/api/favorites");
        using var comparison = await Authenticated(_faked, fan).GetAsync("/api/comparison");

        var favorite = (await ReadAsync(favorites)).GetProperty("items")[0];
        var listings = favorite.GetProperty("listings");
        Assert.Equal(2, listings.GetArrayLength());
        Assert.Contains(listings.EnumerateArray(), entry => entry.GetProperty("purpose").GetString() == "Sale" && entry.GetProperty("price").GetProperty("amount").GetDecimal() == 5_000_000m);
        var rental = listings.EnumerateArray().Single(entry => entry.GetProperty("purpose").GetString() == "Rent");
        Assert.Equal(2, rental.GetProperty("pricingOptions").GetArrayLength());
        var compared = (await ReadAsync(comparison)).GetProperty("items")[0];
        Assert.Equal(2, compared.GetProperty("listings").GetArrayLength());
    }

    private static SmartProperty.Application.Abstractions.Ai.AiSearchItem AiHit(string externalId, string aiId) => new(
        aiId, externalId, 0.9, "Cairo", "القاهرة", "Maadi", "المعادي", null, null, null, null, "Villa", "فيلا", "Sale", "بيع",
        5_000_000m, "EGP", 150m, 3, 2, null, null, null, null, "Available", "Villa for sale", "Villa for sale", "A villa", "A villa");

    // ---- helpers

    private sealed record World(Guid PropertyId, Guid Owner);

    private static object SaleContent(Guid[] evidence) =>
        new { title = "Villa for sale", description = "A villa", amount = 5_000_000m, currency = "EGP", evidenceIds = evidence };

    private static object RentContent(Guid[] evidence) => new
    {
        title = "Villa for rent",
        description = "A villa",
        evidenceIds = evidence,
        pricingOptions = new object[]
        {
            new { period = "Monthly", amount = 20_000m, currency = "EGP" },
            new { period = "Daily", amount = 1_000m, currency = "EGP" },
        },
        rentalTerms = new
        {
            checkInTime = "14:00:00",
            checkOutTime = "11:00:00",
            windows = new object[] { new { startDate = "2027-06-01", endDate = "2027-09-01" } },
        },
    };

    private static object SaleDraft(World world, Guid[] evidence) =>
        new { propertyId = world.PropertyId, purpose = "Sale", content = SaleContent(evidence) };

    private static object RentDraft(World world, Guid[] evidence) =>
        new { propertyId = world.PropertyId, purpose = "Rent", content = RentContent(evidence) };

    private async Task<Guid> SubmittedAsync(WebApplicationFactory<Program> host, World world, ListingPurpose purpose)
    {
        using var client = Authenticated(host, world.Owner);
        var body = purpose == ListingPurpose.Sale
            ? SaleDraft(world, [Guid.NewGuid()])
            : RentDraft(world, [Guid.NewGuid()]);

        using var created = await client.PostAsJsonAsync("/api/marketplace/me/listing-submissions", body);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var draft = await ReadAsync(created);
        var id = draft.GetProperty("submissionId").GetGuid();

        using var submitted = await client.PostAsJsonAsync(
            $"/api/marketplace/me/listing-submissions/{id}/submit", new { expectedRevision = draft.GetProperty("revision").GetInt32() });
        Assert.Equal(HttpStatusCode.OK, submitted.StatusCode);

        return id;
    }

    private HttpClient Authenticated(WebApplicationFactory<Program> host, Guid userId) =>
        AuthScenario.Authenticate(host.CreateClient(), TestJwt.CreateAccessToken(userId));

    private static async Task<JsonElement> ReadAsync(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();

    private async Task<Guid> ActiveUserAsync()
    {
        using var client = _production.CreateClient();

        return (await _scenario.RegisterActiveUserAsync(client)).UserId;
    }

    private async Task<Guid> AdministratorAsync()
    {
        var user = await ActiveUserAsync();
        await _scenario.GrantPlatformPermissionAsync(user, PermissionCodes.MarketplaceSubmissionsReviewRead);
        await _scenario.GrantPlatformPermissionAsync(user, PermissionCodes.MarketplaceSubmissionsReviewDecide);

        return user;
    }

    private async Task<World> SeedAsync(ListingPurpose[] grants)
    {
        var owner = await ActiveUserAsync();
        var issuer = await ActiveUserAsync();
        var legalReviewer = await ActiveUserAsync();
        var at = DateTimeOffset.UtcNow.AddDays(-30);
        // A property the AI service can take: it has an area (with the service's own id), an area in square metres and
        // room counts. Its own area keeps each test's rows apart in the shared database.
        var aiId = Random.Shared.Next(10_000_000, 2_000_000_000);
        var country = new SmartProperty.Domain.Locations.Country(Guid.NewGuid(), aiId, $"Country {aiId}", "بلد", "EG");
        var city = new SmartProperty.Domain.Locations.City(Guid.NewGuid(), aiId, country, "Cairo", "القاهرة");
        var area = new SmartProperty.Domain.Locations.Area(Guid.NewGuid(), aiId, city, "Maadi", "المعادي");
        var property = new Property(
            Guid.NewGuid(),
            PropertyType.Villa,
            new PropertyAddress("EG", city: "Cairo", region: "Cairo Governorate", district: "Maadi"),
            at,
            bedrooms: 3,
            bathrooms: 2,
            physical: new PropertyPhysicalInfo(areaSqm: 150m, timeZoneId: "Africa/Cairo"),
            area: area);
        await using var context = _fixture.CreateContext();
        context.Areas.Add(area);
        context.Properties.Add(property);
        await context.SaveChangesAsync();

        foreach (var purpose in grants)
        {
            var now = DateTimeOffset.UtcNow;
            var identity = new ListingOwnerIdentity(ListingOwnerKind.Individual, Guid.NewGuid(), owner);
            var authority = new ListingLegalAuthority(
                property.Id, purpose, identity, owner, ListingRepresentationKind.Owner, null, now.AddDays(-20), now.AddDays(300));
            ListingReviewContext Reviewer(Guid id) => new(id, owner, identity, null, true, true, null, false);
            context.PropertyAuthorityGrants.Add(PropertyAuthorityGrant.Issue(
                Guid.NewGuid(), authority, Reviewer(issuer), Guid.NewGuid(), Reviewer(legalReviewer),
                now.AddDays(-6), now.AddDays(-5), legalAuthorityVerified: true));
        }

        await context.SaveChangesAsync();

        return new World(property.Id, owner);
    }

    /// <summary>Test-only provider: resolves and cleans whatever it is asked about.</summary>
    private sealed class AnsweringEvidence : IEvidenceAssessmentProvider
    {
        public Task<EvidenceResolution> ResolveAsync(
            Guid actingUserId, Guid propertyId, ListingPurpose purpose, IReadOnlyCollection<Guid> evidenceIds,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(EvidenceResolution.Resolved(evidenceIds
                .Select(id => new ListingEvidenceReference(id, Guid.NewGuid(), propertyId, purpose, new string('A', 64)))
                .ToArray()));

        public Task<EvidenceAssessmentResult> AssessAsync(
            IReadOnlyCollection<ListingEvidenceReference> evidence, DateTimeOffset at, CancellationToken cancellationToken = default) =>
            Task.FromResult(EvidenceAssessmentResult.Assessed(evidence
                .Select(reference => new ListingEvidenceAssessment(Guid.NewGuid(), reference, EvidenceScanStatus.Clean, at))
                .ToArray()));
    }

    /// <summary>Test-only provider: every reviewer is verified, unconflicted and unaffiliated.</summary>
    private sealed class IndependentReviewer : IReviewerIndependenceProvider
    {
        public Task<ReviewerIndependenceFacts> GetAsync(
            Guid reviewerUserId, Guid submitterUserId, ListingOwnerIdentity declaredOwner, Guid? submittingWorkspaceId, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ReviewerIndependenceFacts(true, false, null));
    }
}
