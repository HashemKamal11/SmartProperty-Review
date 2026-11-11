using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using SmartProperty.Api.Contracts;
using SmartProperty.Api.Contracts.OwnerVerification;
using SmartProperty.Api.Contracts.PropertyRegistry;
using SmartProperty.Api.IntegrationTests.Infrastructure;
using SmartProperty.Application.Authorization;
using SmartProperty.Domain.Identity;
using SmartProperty.Domain.OwnerVerification;
using Xunit;

namespace SmartProperty.Api.IntegrationTests.Authorization;

/// <summary>
/// Owner verification through the real host and database: the three steps, the review, and what approval unlocks.
/// </summary>
[Collection(ApiPostgreSqlCollection.Name)]
public sealed class OwnerVerificationEndpointTests : IDisposable
{
    private const string MyRoute = "/api/owner-profile";
    private const string AdminRoute = "/api/admin/owner-verifications";

    private static readonly byte[] PngBytes =
        [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 0, 1, 2, 3, 4];

    private readonly ApiPostgreSqlFixture _fixture;
    private readonly SmartPropertyApiFactory _factory;

    public OwnerVerificationEndpointTests(ApiPostgreSqlFixture fixture)
    {
        _fixture = fixture;
        _factory = new SmartPropertyApiFactory(fixture);
    }

    public void Dispose()
    {
        _factory.Dispose();
    }

    // ---- who may call

    [Fact]
    public async Task EveryEndpointRequiresSigningIn()
    {
        using var client = _factory.CreateClient();
        var id = Guid.NewGuid();

        var responses = new[]
        {
            await client.GetAsync(MyRoute),
            await client.PutAsJsonAsync($"{MyRoute}/identity", new { }),
            await client.PutAsJsonAsync($"{MyRoute}/address", new { }),
            await client.GetAsync($"{MyRoute}/address-proof"),
            await client.PostAsync($"{MyRoute}/submit", content: null),
            await client.GetAsync(AdminRoute),
            await client.GetAsync($"{AdminRoute}/{id}"),
            await client.PostAsync($"{AdminRoute}/{id}/approve", content: null),
        };

        Assert.All(responses, response => Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode));
    }

    [Fact]
    public async Task ReviewingNeedsTheReviewPermissionAndSigningInIsNotEnough()
    {
        var userId = await SeedUserAsync();
        using var client = _factory.CreateAuthenticatedClient(userId);
        var id = Guid.NewGuid();

        var responses = new[]
        {
            await client.GetAsync(AdminRoute),
            await client.GetAsync($"{AdminRoute}/{id}"),
            await client.GetAsync($"{AdminRoute}/{id}/address-proof"),
            await client.PostAsync($"{AdminRoute}/{id}/approve", content: null),
            await client.PostAsJsonAsync($"{AdminRoute}/{id}/reject", new RejectOwnerProfileRequest("no")),
        };

        Assert.All(responses, response => Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode));
    }

    [Fact]
    public async Task AnyoneSignedInMayFillInTheirOwnProfileWithoutAnyPermission()
    {
        using var client = _factory.CreateAuthenticatedClient(await SeedUserAsync());

        using var response = await client.GetAsync(MyRoute);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // ---- the three steps

    [Fact]
    public async Task ANewUserSeesNotStartedWithTheirAccountData()
    {
        var userId = await SeedUserAsync();
        using var client = _factory.CreateAuthenticatedClient(userId);

        var profile = (await client.GetFromJsonAsync<OwnerProfileResponse>(MyRoute))!;

        Assert.Equal("NotStarted", profile.Status);
        Assert.False(profile.CanAddProperties);
        Assert.Null(profile.ProfileId);
        Assert.Equal("Abdullah", profile.Account.FirstName);
        Assert.Null(profile.Identity);
        Assert.Null(profile.Address);
    }

    [Fact]
    public async Task TheStepsAreSavedAndReadBack()
    {
        using var client = _factory.CreateAuthenticatedClient(await SeedUserAsync());

        var nationalId = UniqueNationalId();
        using var identity = await client.PutAsJsonAsync($"{MyRoute}/identity", IdentityBody(nationalId));
        using var address = await client.PutAsJsonAsync($"{MyRoute}/address", AddressBody());
        using var proof = await UploadProofAsync(client, PngBytes);

        Assert.Equal(HttpStatusCode.OK, identity.StatusCode);
        Assert.Equal(HttpStatusCode.OK, address.StatusCode);
        Assert.Equal(HttpStatusCode.OK, proof.StatusCode);

        var profile = (await client.GetFromJsonAsync<OwnerProfileResponse>(MyRoute))!;
        Assert.Equal("Draft", profile.Status);
        Assert.Equal(nationalId, profile.Identity!.NationalId);
        Assert.Equal(new DateOnly(1990, 1, 1), profile.Identity.BirthDate);
        Assert.Equal("Al Olaya", profile.Address!.StreetName);
        Assert.True(profile.Address.HasProofImage);
    }

    [Theory]
    [InlineData("123", "Saudi")]
    [InlineData("1234567890", "")]
    public async Task InvalidIdentityReturns422(string nationalId, string nationality)
    {
        using var client = _factory.CreateAuthenticatedClient(await SeedUserAsync());

        using var response = await client.PutAsJsonAsync(
            $"{MyRoute}/identity",
            IdentityBody() with { NationalId = nationalId == "1234567890" ? UniqueNationalId() : nationalId, Nationality = nationality });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var error = (await response.Content.ReadFromJsonAsync<ApiErrorResponse>())!;
        Assert.Equal("validation.failed", error.Code);
    }

    [Fact]
    public async Task AnEgyptianIdIsSavedAndReadBackWithItsCountry()
    {
        using var client = _factory.CreateAuthenticatedClient(await SeedUserAsync());
        // Century 2 (1900s), born 1990-01-01, then a random governorate and sequence so the ID is unique per run.
        var nationalId = $"2900101{Random.Shared.Next(1_000_000, 9_999_999)}";

        using var response = await client.PutAsJsonAsync(
            $"{MyRoute}/identity",
            IdentityBody() with { IdCountryCode = "eg", NationalId = nationalId, Nationality = "Egyptian" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var profile = (await response.Content.ReadFromJsonAsync<OwnerProfileResponse>())!;
        Assert.Equal("EG", profile.Identity!.IdCountryCode);
        Assert.Equal(nationalId, profile.Identity.NationalId);
    }

    [Theory]
    [InlineData("EG", "29001020101234")] // encodes 1990-01-02, the body says 1990-01-01
    [InlineData("AE", "123199012345671")]
    [InlineData("US", "1234567890")]
    [InlineData(null, "1234567890")]
    public async Task AnIdThatDoesNotFitItsCountryReturns422(string? country, string nationalId)
    {
        using var client = _factory.CreateAuthenticatedClient(await SeedUserAsync());

        using var response = await client.PutAsJsonAsync(
            $"{MyRoute}/identity",
            IdentityBody() with { IdCountryCode = country, NationalId = nationalId });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }

    [Fact]
    public async Task AOneDigitBuildingNumberWithoutAnAdditionalNumberIsSaved()
    {
        using var client = _factory.CreateAuthenticatedClient(await SeedUserAsync());

        using var response = await client.PutAsJsonAsync(
            $"{MyRoute}/address",
            AddressBody() with { BuildingNumber = "7", AdditionalNumber = null });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var profile = (await response.Content.ReadFromJsonAsync<OwnerProfileResponse>())!;
        Assert.Equal("7", profile.Address!.BuildingNumber);
        Assert.Null(profile.Address.AdditionalNumber);
    }

    [Fact]
    public async Task InvalidAddressReturns422()
    {
        using var client = _factory.CreateAuthenticatedClient(await SeedUserAsync());

        using var response = await client.PutAsJsonAsync(
            $"{MyRoute}/address",
            AddressBody() with { PostalCode = "12" });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }

    [Fact]
    public async Task ANationalIdAlreadyRegisteredToAnotherAccountIs409()
    {
        using var first = _factory.CreateAuthenticatedClient(await SeedUserAsync());
        using var second = _factory.CreateAuthenticatedClient(await SeedUserAsync());
        var shared = UniqueNationalId();
        (await first.PutAsJsonAsync($"{MyRoute}/identity", IdentityBody(shared))).Dispose();

        using var response = await second.PutAsJsonAsync($"{MyRoute}/identity", IdentityBody(shared));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var error = (await response.Content.ReadFromJsonAsync<ApiErrorResponse>())!;
        Assert.Equal("owner_profile.national_id_taken", error.Code);
    }

    [Fact]
    public async Task AFileThatIsNotAnImageReturns422AndStoresNothing()
    {
        using var client = _factory.CreateAuthenticatedClient(await SeedUserAsync());

        using var response = await UploadProofAsync(client, "%PDF-1.7 definitely not an image"u8.ToArray());

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Empty(Directory.Exists(_factory.ImageRoot)
            ? Directory.GetFiles(_factory.ImageRoot, "*", SearchOption.AllDirectories)
            : []);
    }

    [Fact]
    public async Task ARequestWithNoFileReturns422()
    {
        using var client = _factory.CreateAuthenticatedClient(await SeedUserAsync());
        // A form with some other field, so it parses and the missing file is what the handler reports.
        using var empty = new MultipartFormDataContent { { new StringContent("x"), "other" } };

        using var response = await client.PutAsync($"{MyRoute}/address-proof", empty);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }

    [Fact]
    public async Task TheOwnerGetsTheirProofBackUncachedAndOnlyTheirOwn()
    {
        var owner = _factory.CreateAuthenticatedClient(await SeedUserAsync());
        var other = _factory.CreateAuthenticatedClient(await SeedUserAsync());
        (await UploadProofAsync(owner, PngBytes)).Dispose();

        using var mine = await owner.GetAsync($"{MyRoute}/address-proof");
        using var theirs = await other.GetAsync($"{MyRoute}/address-proof");

        Assert.Equal(HttpStatusCode.OK, mine.StatusCode);
        Assert.Equal("image/png", mine.Content.Headers.ContentType!.MediaType);
        Assert.Equal(PngBytes, await mine.Content.ReadAsByteArrayAsync());
        Assert.Contains("no-store", mine.Headers.CacheControl!.ToString());
        Assert.Equal(HttpStatusCode.NotFound, theirs.StatusCode);
        owner.Dispose();
        other.Dispose();
    }

    // ---- submit

    [Fact]
    public async Task SubmittingAnIncompleteProfileReturns422()
    {
        using var client = _factory.CreateAuthenticatedClient(await SeedUserAsync());

        using var nothing = await client.PostAsync($"{MyRoute}/submit", content: null);
        (await client.PutAsJsonAsync($"{MyRoute}/identity", IdentityBody())).Dispose();
        (await client.PutAsJsonAsync($"{MyRoute}/address", AddressBody())).Dispose();
        using var noProof = await client.PostAsync($"{MyRoute}/submit", content: null);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, nothing.StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, noProof.StatusCode);
        Assert.Equal("owner_profile.address_incomplete", (await noProof.Content.ReadFromJsonAsync<ApiErrorResponse>())!.Code);
    }

    [Fact]
    public async Task ASubmittedProfileIsLockedUntilAReviewerDecides()
    {
        using var client = _factory.CreateAuthenticatedClient(await SeedUserAsync());
        await CompleteAndSubmitAsync(client);

        using var edit = await client.PutAsJsonAsync($"{MyRoute}/identity", IdentityBody() with { Nationality = "Egyptian" });
        using var again = await client.PostAsync($"{MyRoute}/submit", content: null);

        Assert.Equal(HttpStatusCode.Conflict, edit.StatusCode);
        Assert.Equal("owner_profile.locked_for_review", (await edit.Content.ReadFromJsonAsync<ApiErrorResponse>())!.Code);
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
    }

    // ---- review

    [Fact]
    public async Task TheQueueShowsSubmittedProfilesAndNoDraftsAndNeverTheIdNumber()
    {
        using var owner = _factory.CreateAuthenticatedClient(await SeedUserAsync());
        using var drafter = _factory.CreateAuthenticatedClient(await SeedUserAsync());
        var draftId = UniqueNationalId();
        (await drafter.PutAsJsonAsync($"{MyRoute}/identity", IdentityBody(draftId))).Dispose();
        var submitted = await CompleteAndSubmitAsync(owner);
        using var reviewer = ReviewerClient(await SeedUserAsync());

        using var response = await reviewer.GetAsync($"{AdminRoute}?status=UnderReview");
        var json = await response.Content.ReadAsStringAsync();
        var page = System.Text.Json.JsonSerializer.Deserialize<PagedResponse<OwnerReviewItemResponse>>(
            json,
            new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web))!;

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var item = Assert.Single(page.Items, candidate => candidate.ProfileId == submitted.ProfileId);
        Assert.Equal("UnderReview", item.Status);
        Assert.DoesNotContain(submitted.Identity!.NationalId, json);
        Assert.DoesNotContain(draftId, json);
    }

    [Fact]
    public async Task TheQueueRefusesAnUnknownOrDraftStatus()
    {
        using var reviewer = ReviewerClient(await SeedUserAsync());

        using var draft = await reviewer.GetAsync($"{AdminRoute}?status=Draft");

        Assert.Equal(HttpStatusCode.UnprocessableEntity, draft.StatusCode);
    }

    [Fact]
    public async Task AReviewerSeesTheEvidenceAndApprovalUnlocksTheOwner()
    {
        var ownerId = await SeedUserAsync();
        using var owner = _factory.CreateAuthenticatedClient(ownerId);
        var submitted = await CompleteAndSubmitAsync(owner);
        using var reviewer = ReviewerClient(await SeedUserAsync());

        var detail = (await reviewer.GetFromJsonAsync<OwnerProfileResponse>($"{AdminRoute}/{submitted.ProfileId}"))!;
        using var proof = await reviewer.GetAsync($"{AdminRoute}/{submitted.ProfileId}/address-proof");
        using var approve = await reviewer.PostAsync($"{AdminRoute}/{submitted.ProfileId}/approve", content: null);

        Assert.Equal(submitted.Identity!.NationalId, detail.Identity!.NationalId);
        Assert.Equal("Riyadh", detail.Address!.City);
        Assert.Equal(PngBytes, await proof.Content.ReadAsByteArrayAsync());
        Assert.Equal(HttpStatusCode.OK, approve.StatusCode);

        var mine = (await owner.GetFromJsonAsync<OwnerProfileResponse>(MyRoute))!;
        Assert.Equal("Approved", mine.Status);
        Assert.True(mine.CanAddProperties);

        await using var context = _fixture.CreateContext();
        var roleNames = await (
            from assignment in context.UserPlatformRoles
            join role in context.Roles on assignment.RoleId equals role.Id
            where assignment.UserId == ownerId
            select role.Name).ToListAsync();
        Assert.Equal(["Property Owner"], roleNames);
    }

    [Fact]
    public async Task RejectionNeedsAReasonAndTheOwnerSeesItThenCorrectsAndResubmits()
    {
        using var owner = _factory.CreateAuthenticatedClient(await SeedUserAsync());
        var submitted = await CompleteAndSubmitAsync(owner);
        using var reviewer = ReviewerClient(await SeedUserAsync());

        using var blank = await reviewer.PostAsJsonAsync($"{AdminRoute}/{submitted.ProfileId}/reject", new RejectOwnerProfileRequest(" "));
        using var reject = await reviewer.PostAsJsonAsync($"{AdminRoute}/{submitted.ProfileId}/reject", new RejectOwnerProfileRequest("The street is wrong."));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, blank.StatusCode);
        Assert.Equal(HttpStatusCode.OK, reject.StatusCode);

        var seen = (await owner.GetFromJsonAsync<OwnerProfileResponse>(MyRoute))!;
        Assert.Equal("Rejected", seen.Status);
        Assert.Equal("The street is wrong.", seen.RejectionReason);
        Assert.False(seen.CanAddProperties);

        using var fix = await owner.PutAsJsonAsync($"{MyRoute}/address", AddressBody() with { StreetName = "Right street" });
        using var resubmit = await owner.PostAsync($"{MyRoute}/submit", content: null);

        Assert.Equal("Draft", (await fix.Content.ReadFromJsonAsync<OwnerProfileResponse>())!.Status);
        Assert.Equal("UnderReview", (await resubmit.Content.ReadFromJsonAsync<OwnerProfileResponse>())!.Status);
    }

    [Fact]
    public async Task OnlyAProfileUnderReviewCanBeDecidedAndADraftIsNotFound()
    {
        using var owner = _factory.CreateAuthenticatedClient(await SeedUserAsync());
        (await owner.PutAsJsonAsync($"{MyRoute}/identity", IdentityBody())).Dispose();
        var draftId = (await owner.GetFromJsonAsync<OwnerProfileResponse>(MyRoute))!.ProfileId;
        using var reviewer = ReviewerClient(await SeedUserAsync());

        using var draft = await reviewer.PostAsync($"{AdminRoute}/{draftId}/approve", content: null);
        using var unknown = await reviewer.PostAsync($"{AdminRoute}/{Guid.NewGuid()}/approve", content: null);

        Assert.Equal(HttpStatusCode.NotFound, draft.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
    }

    [Fact]
    public async Task AReviewerCannotApproveTheirOwnProfile()
    {
        var reviewerId = await SeedUserAsync();
        using var asOwner = _factory.CreateAuthenticatedClient(reviewerId);
        var submitted = await CompleteAndSubmitAsync(asOwner);
        using var reviewer = ReviewerClient(reviewerId);

        using var response = await reviewer.PostAsync($"{AdminRoute}/{submitted.ProfileId}/approve", content: null);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("owner_profile.cannot_review_own", (await response.Content.ReadFromJsonAsync<ApiErrorResponse>())!.Code);
    }

    // ---- what approval unlocks: a property remembers who registered it

    [Fact]
    public async Task ACreatedPropertyRecordsItsOwner()
    {
        var ownerId = await SeedUserAsync();
        _factory.PermissionChecker.AllowedPermissionCodes.Add(PermissionCodes.PropertyCreate);
        using var client = _factory.CreateAuthenticatedClient(ownerId);

        var place = TestPlaces.Seed(_fixture);
        using var response = await client.PostAsJsonAsync("/api/properties", new CreatePropertyRequest("Land", place.AreaId));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var created = (await response.Content.ReadFromJsonAsync<CreatePropertyResponse>())!;
        await using var context = _fixture.CreateContext();
        var stored = await context.Properties.AsNoTracking().SingleAsync(property => property.Id == created.Id);
        Assert.Equal(ownerId, stored.OwnerUserId);
    }

    // ---- helpers

    private HttpClient ReviewerClient(Guid reviewerId)
    {
        _factory.PermissionChecker.AllowedPermissionCodes.Add(PermissionCodes.OwnerVerificationReview);

        return _factory.CreateAuthenticatedClient(reviewerId);
    }

    // The tests share one database and a national ID belongs to one account, so each call gets an ID of its own.
    // Saudi IDs start with 1 (citizen) or 2 (resident).
    private static string UniqueNationalId() => $"{Random.Shared.NextInt64(1_000_000_000, 2_999_999_999)}";

    private static SaveOwnerIdentityRequest IdentityBody(string? nationalId = null)
    {
        return new SaveOwnerIdentityRequest("SA", nationalId ?? UniqueNationalId(), "Saudi", new DateOnly(1990, 1, 1), new DateOnly(2032, 1, 1), true);
    }

    private static SaveOwnerAddressRequest AddressBody()
    {
        return new SaveOwnerAddressRequest("1234", "Al Olaya", "Al Malqa", "Riyadh", "12345", "5678", true);
    }

    private static async Task<HttpResponseMessage> UploadProofAsync(HttpClient client, byte[] bytes)
    {
        using var content = new MultipartFormDataContent();
        var file = new ByteArrayContent(bytes);
        file.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        content.Add(file, "file", "card.png");

        return await client.PutAsync("/api/owner-profile/address-proof", content);
    }

    private static async Task<OwnerProfileResponse> CompleteAndSubmitAsync(HttpClient client)
    {
        (await client.PutAsJsonAsync($"{MyRoute}/identity", IdentityBody())).EnsureSuccessStatusCode();
        (await client.PutAsJsonAsync($"{MyRoute}/address", AddressBody())).EnsureSuccessStatusCode();
        (await UploadProofAsync(client, PngBytes)).EnsureSuccessStatusCode();
        var submitted = await client.PostAsync($"{MyRoute}/submit", content: null);
        submitted.EnsureSuccessStatusCode();

        return (await submitted.Content.ReadFromJsonAsync<OwnerProfileResponse>())!;
    }

    private async Task<Guid> SeedUserAsync()
    {
        var user = new User(Guid.NewGuid(), $"{Guid.NewGuid():n}@example.com", "Abdullah", "Salem", DateTimeOffset.UtcNow);
        user.Activate(DateTimeOffset.UtcNow);

        await using var context = _fixture.CreateContext();
        context.Users.Add(user);
        await context.SaveChangesAsync();

        return user.Id;
    }
}
