using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using SmartProperty.Api.Contracts;
using SmartProperty.Api.Contracts.PropertyRegistry;
using SmartProperty.Api.IntegrationTests.Infrastructure;
using SmartProperty.Application.Abstractions.Ai;
using SmartProperty.Application.Authorization;
using SmartProperty.Domain.Locations;
using SmartProperty.Domain.PropertyRegistry;
using Xunit;

namespace SmartProperty.Api.IntegrationTests.PropertyRegistry;

/// <summary>
/// Covers flow 1: a property is offered to the AI search service when it is created, updated, or archived, and the
/// outcome is stored on the property and returned as <c>aiSync</c>. The AI service is always the fake.
/// </summary>
[Collection(ApiPostgreSqlCollection.Name)]
public sealed class PropertyAiSyncEndpointTests : IDisposable
{
    private const string Route = "/api/properties";

    private readonly ApiPostgreSqlFixture _fixture;
    private readonly SmartPropertyApiFactory _factory;

    public PropertyAiSyncEndpointTests(ApiPostgreSqlFixture fixture)
    {
        _fixture = fixture;
        _factory = new SmartPropertyApiFactory(fixture);
    }

    public void Dispose()
    {
        _factory.Dispose();
    }

    [Fact]
    public async Task ACompletePropertyIsSentToTheAiServiceInItsVocabularyAndReportedAsSynced()
    {
        using var client = AuthorizedClient();

        using var response = await client.PostAsJsonAsync(Route, await CompleteRequestAsync());

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = (await response.Content.ReadFromJsonAsync<CreatePropertyResponse>())!;
        Assert.Equal("Synced", body.AiSync!.Status);
        Assert.NotNull(body.AiSync.SyncedAt);
        Assert.Null(body.AiSync.Error);

        var (externalId, listing) = Assert.Single(_factory.AiGateway.Puts);
        Assert.Equal(body.Id.ToString(), externalId);
        Assert.Equal("Twin House", listing.PropertyType);
        Assert.Equal("Sale", listing.TransactionType);
        Assert.Equal(4_500_000m, listing.Price);
        Assert.Equal(180m, listing.AreaSqm);
        Assert.Equal(3, listing.Bedrooms);
        Assert.Equal(2, listing.Bathrooms);
        Assert.Equal(5, listing.Floor);
        Assert.Equal("Luxury Finished", listing.FinishingStatus);
        Assert.Equal(false, listing.Furnished);
        Assert.Equal(_area!.AiId, listing.AreaId);
        Assert.Equal(_compound!.AiId, listing.CompoundId);
        Assert.Equal("City", listing.View);
        Assert.Equal(["balcony", "pool"], listing.Amenities);
        Assert.Equal("Available", listing.ListingStatus);

        var persisted = await LoadAsync(body.Id);
        Assert.Equal(AiSyncStatus.Synced, persisted.AiSyncStatus);
        Assert.NotNull(persisted.AiSyncedAt);
    }

    [Fact]
    public async Task AnIncompletePropertyIsSavedButNotSentAndSaysWhatIsMissing()
    {
        using var client = AuthorizedClient();

        using var response = await client.PostAsJsonAsync(Route, new CreatePropertyRequest("Land", "EG"));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = (await response.Content.ReadFromJsonAsync<CreatePropertyResponse>())!;
        Assert.Equal("NotSynced", body.AiSync!.Status);
        Assert.Contains("missing transactionType, price, areaSqm, areaId, bedrooms, bathrooms", body.AiSync.Error);
        Assert.Empty(_factory.AiGateway.Puts);
        Assert.Equal(AiSyncStatus.NotSynced, (await LoadAsync(body.Id)).AiSyncStatus);
    }

    [Fact]
    public async Task AnAiOutageNeverLosesTheProperty()
    {
        _factory.AiGateway.PutError = AiSearchErrors.Unavailable("it could not be reached");
        using var client = AuthorizedClient();

        using var response = await client.PostAsJsonAsync(Route, await CompleteRequestAsync());

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = (await response.Content.ReadFromJsonAsync<CreatePropertyResponse>())!;
        Assert.Equal("Failed", body.AiSync!.Status);
        Assert.Contains("could not be reached", body.AiSync.Error);

        var persisted = await LoadAsync(body.Id);
        Assert.Equal(PropertyStatus.Active, persisted.Status);
        Assert.Equal(AiSyncStatus.Failed, persisted.AiSyncStatus);
    }

    [Fact]
    public async Task ADisabledIntegrationLeavesPropertiesNotSyncedWithoutFailing()
    {
        _factory.AiGateway.PutError = AiSearchErrors.Disabled;
        using var client = AuthorizedClient();

        using var response = await client.PostAsJsonAsync(Route, await CompleteRequestAsync());

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = (await response.Content.ReadFromJsonAsync<CreatePropertyResponse>())!;
        Assert.Equal("NotSynced", body.AiSync!.Status);
        Assert.Contains("disabled", body.AiSync.Error);
    }

    [Fact]
    public async Task CreateReturnsTheListingViewsAndEveryFactItStored()
    {
        using var client = AuthorizedClient();

        using var response = await client.PostAsJsonAsync(Route, await CompleteRequestAsync());

        var body = (await response.Content.ReadFromJsonAsync<CreatePropertyResponse>())!;
        Assert.Equal("TwinHouse", body.Type);
        Assert.Equal("LuxuryFinished", body.FinishingStatus);
        Assert.Equal(["City", "Sea"], body.Views!.Select(view => view.Code));
        Assert.Equal("مدينة", body.Views![0].NameAr);
        Assert.Equal("Sale", body.Listing!.TransactionType);
        Assert.Equal(4_500_000m, body.Listing.Price);
        Assert.Equal(180m, body.Listing.AreaSqm);
        Assert.Equal(5, body.Listing.Floor);
        Assert.Equal(false, body.Listing.Furnished);
        Assert.Equal("Available", body.Listing.ListingStatus);
        Assert.Equal("عنوان", body.Listing.TitleAr);
        Assert.Equal(_area!.Id, body.Place!.Area.Id);
        Assert.Equal(_compound!.Id, body.Place.Compound!.Id);
        Assert.Equal("Cairo", body.Place.City.NameEn);
        Assert.Equal("Egypt", body.Place.Country.NameEn);
        Assert.Equal(_area.NameAr, body.Place.Area.NameAr);

        var persisted = await LoadAsync(body.Id);
        Assert.Equal(4_500_000m, persisted.Listing.Price);
        Assert.Equal(["City", "Sea"], persisted.Views.OrderBy(view => view.SortOrder).Select(view => view.Code));
    }

    [Fact]
    public async Task GetByIdAndListReturnTheListingViewsAndSyncState()
    {
        using var client = AuthorizedClient();
        using var created = await client.PostAsJsonAsync(Route, await CompleteRequestAsync());
        var id = (await created.Content.ReadFromJsonAsync<CreatePropertyResponse>())!.Id;

        var single = (await client.GetFromJsonAsync<PropertyResponse>($"{Route}/{id}"))!;
        Assert.Equal("Synced", single.AiSync!.Status);
        Assert.Equal(4_500_000m, single.Listing!.Price);
        Assert.Equal(["City", "Sea"], single.Views!.Select(view => view.Code));

        var page = (await client.GetFromJsonAsync<PagedResponse<PropertyResponse>>($"{Route}?pageSize=100"))!;
        var listed = Assert.Single(page.Items, item => item.Id == id);
        Assert.Equal("Synced", listed.AiSync!.Status);
        Assert.Equal(4_500_000m, listed.Listing!.Price);
        Assert.Equal(["City", "Sea"], listed.Views!.Select(view => view.Code));
    }

    [Theory]
    [InlineData("Unit")]
    [InlineData("apartment")]
    [InlineData("Twin House")]
    public async Task CreateRefusesTypesOutsideTheAiList(string type)
    {
        using var client = AuthorizedClient();

        using var response = await client.PostAsJsonAsync(Route, new CreatePropertyRequest(type, "EG"));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Empty(_factory.AiGateway.Puts);
    }

    [Fact]
    public async Task CreateRefusesAnUnknownViewCode()
    {
        using var client = AuthorizedClient();

        using var response = await client.PostAsJsonAsync(
            Route, new CreatePropertyRequest("Villa", "EG", ViewCodes: ["Moon"]));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var error = (await response.Content.ReadFromJsonAsync<ApiErrorResponse>())!;
        Assert.Contains("Moon", error.Message);
    }

    [Theory]
    [InlineData("Buy", null, null)]
    [InlineData(null, -1, null)]
    [InlineData(null, null, 0)]
    public async Task CreateRefusesAnInvalidListingField(string? transaction, int? price, int? area)
    {
        using var client = AuthorizedClient();

        using var response = await client.PostAsJsonAsync(
            Route,
            new CreatePropertyRequest("Villa", "EG", TransactionType: transaction, Price: price, AreaSqm: area));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }

    [Fact]
    public async Task UpdateSendsTheChangedPropertyAgain()
    {
        using var client = AuthorizedClient();
        using var created = await client.PostAsJsonAsync(Route, await CompleteRequestAsync());
        var id = (await created.Content.ReadFromJsonAsync<CreatePropertyResponse>())!.Id;

        using var response = await client.PutAsJsonAsync(
            $"{Route}/{id}",
            ToUpdate(await CompleteRequestAsync()) with { Price = 4_800_000m });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = (await response.Content.ReadFromJsonAsync<PropertyResponse>())!;
        Assert.Equal(4_800_000m, body.Listing!.Price);
        Assert.Equal("Synced", body.AiSync!.Status);
        Assert.Equal(2, _factory.AiGateway.Puts.Count);
        Assert.Equal(4_800_000m, _factory.AiGateway.Puts.Last().Listing.Price);
    }

    [Fact]
    public async Task AnUnchangedUpdateOfASyncedPropertyDoesNotCallTheAiServiceAgain()
    {
        using var client = AuthorizedClient();
        using var created = await client.PostAsJsonAsync(Route, await CompleteRequestAsync());
        var id = (await created.Content.ReadFromJsonAsync<CreatePropertyResponse>())!.Id;

        using var response = await client.PutAsJsonAsync($"{Route}/{id}", ToUpdate(await CompleteRequestAsync()));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Single(_factory.AiGateway.Puts);
    }

    [Fact]
    public async Task SavingAFailedPropertyAgainRepairsIt()
    {
        _factory.AiGateway.PutError = AiSearchErrors.Unavailable("it could not be reached");
        using var client = AuthorizedClient();
        using var created = await client.PostAsJsonAsync(Route, await CompleteRequestAsync());
        var id = (await created.Content.ReadFromJsonAsync<CreatePropertyResponse>())!.Id;
        _factory.AiGateway.PutError = null;

        using var response = await client.PutAsJsonAsync($"{Route}/{id}", ToUpdate(await CompleteRequestAsync()));

        var body = (await response.Content.ReadFromJsonAsync<PropertyResponse>())!;
        Assert.Equal("Synced", body.AiSync!.Status);
        Assert.Null(body.AiSync.Error);
    }

    [Fact]
    public async Task ArchivingASyncedPropertyTakesItOffTheAiService()
    {
        _factory.PermissionChecker.AllowedPermissionCodes.Add(PermissionCodes.PropertyArchive);
        using var client = AuthorizedClient();
        using var created = await client.PostAsJsonAsync(Route, await CompleteRequestAsync());
        var id = (await created.Content.ReadFromJsonAsync<CreatePropertyResponse>())!.Id;

        using var response = await client.PostAsync($"{Route}/{id}/archive", content: null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal([id.ToString()], _factory.AiGateway.Deletes.ToArray());
        var body = (await response.Content.ReadFromJsonAsync<PropertyResponse>())!;
        Assert.Equal("Archived", body.Status);
        Assert.Equal("NotSynced", body.AiSync!.Status);
    }

    [Fact]
    public async Task ArchivingAPropertyThatWasNeverSentCallsNothing()
    {
        _factory.PermissionChecker.AllowedPermissionCodes.Add(PermissionCodes.PropertyArchive);
        using var client = AuthorizedClient();
        using var created = await client.PostAsJsonAsync(Route, new CreatePropertyRequest("Land", "EG"));
        var id = (await created.Content.ReadFromJsonAsync<CreatePropertyResponse>())!.Id;

        using var response = await client.PostAsync($"{Route}/{id}/archive", content: null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Empty(_factory.AiGateway.Deletes);
    }

    [Fact]
    public async Task AFailedArchiveRemovalIsReportedButTheArchiveStands()
    {
        _factory.PermissionChecker.AllowedPermissionCodes.Add(PermissionCodes.PropertyArchive);
        using var client = AuthorizedClient();
        using var created = await client.PostAsJsonAsync(Route, await CompleteRequestAsync());
        var id = (await created.Content.ReadFromJsonAsync<CreatePropertyResponse>())!.Id;
        _factory.AiGateway.DeleteError = AiSearchErrors.Unavailable("it could not be reached");

        using var response = await client.PostAsync($"{Route}/{id}/archive", content: null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = (await response.Content.ReadFromJsonAsync<PropertyResponse>())!;
        Assert.Equal("Archived", body.Status);
        Assert.Equal("Failed", body.AiSync!.Status);
    }

    [Fact]
    public async Task TheSyncEndpointRepairsAFailedProperty()
    {
        _factory.AiGateway.PutError = AiSearchErrors.Unavailable("it could not be reached");
        using var client = AuthorizedClient();
        using var created = await client.PostAsJsonAsync(Route, await CompleteRequestAsync());
        var id = (await created.Content.ReadFromJsonAsync<CreatePropertyResponse>())!.Id;
        _factory.AiGateway.PutError = null;

        using var response = await client.PostAsync($"{Route}/{id}/ai-sync", content: null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = (await response.Content.ReadFromJsonAsync<PropertyResponse>())!;
        Assert.Equal("Synced", body.AiSync!.Status);
        Assert.Equal(AiSyncStatus.Synced, (await LoadAsync(id)).AiSyncStatus);
    }

    [Fact]
    public async Task TheSyncEndpointReturnsTheOutcomeEvenWhenTheServiceStillFails()
    {
        _factory.AiGateway.PutError = AiSearchErrors.Unavailable("it could not be reached");
        using var client = AuthorizedClient();
        using var created = await client.PostAsJsonAsync(Route, await CompleteRequestAsync());
        var id = (await created.Content.ReadFromJsonAsync<CreatePropertyResponse>())!.Id;

        using var response = await client.PostAsync($"{Route}/{id}/ai-sync", content: null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Failed", (await response.Content.ReadFromJsonAsync<PropertyResponse>())!.AiSync!.Status);
    }

    [Fact]
    public async Task TheSyncEndpointRefusesAnUnknownAndAnArchivedProperty()
    {
        _factory.PermissionChecker.AllowedPermissionCodes.Add(PermissionCodes.PropertyArchive);
        using var client = AuthorizedClient();
        using var created = await client.PostAsJsonAsync(Route, new CreatePropertyRequest("Land", "EG"));
        var id = (await created.Content.ReadFromJsonAsync<CreatePropertyResponse>())!.Id;
        using var archived = await client.PostAsync($"{Route}/{id}/archive", content: null);
        Assert.Equal(HttpStatusCode.OK, archived.StatusCode);

        using var unknown = await client.PostAsync($"{Route}/{Guid.NewGuid()}/ai-sync", content: null);
        using var refused = await client.PostAsync($"{Route}/{id}/ai-sync", content: null);

        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Equal("properties.archived", (await refused.Content.ReadFromJsonAsync<ApiErrorResponse>())!.Code);
    }

    [Fact]
    public async Task TheSyncEndpointNeedsSignInAndTheUpdatePermission()
    {
        var id = (await SeedAsync()).Id;
        using var anonymous = _factory.CreateClient();
        using var withoutPermission = _factory.CreateAuthenticatedClient(Guid.NewGuid());

        using var unauthenticated = await anonymous.PostAsync($"{Route}/{id}/ai-sync", content: null);
        using var forbidden = await withoutPermission.PostAsync($"{Route}/{id}/ai-sync", content: null);

        Assert.Equal(HttpStatusCode.Unauthorized, unauthenticated.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
        Assert.Empty(_factory.AiGateway.Puts);
    }

    private HttpClient AuthorizedClient()
    {
        _factory.PermissionChecker.AllowedPermissionCodes.Add(PermissionCodes.PropertyCreate);
        _factory.PermissionChecker.AllowedPermissionCodes.Add(PermissionCodes.PropertyRead);
        _factory.PermissionChecker.AllowedPermissionCodes.Add(PermissionCodes.PropertyUpdate);
        return _factory.CreateAuthenticatedClient(TestUsers.Seed(_fixture));
    }

    private Area? _area;
    private Compound? _compound;

    /// <summary>
    /// A request for a complete property. Its place is seeded with AI ids no other test uses, because the places of
    /// the shared database are unique by AI id.
    /// </summary>
    private async Task<CreatePropertyRequest> CompleteRequestAsync()
    {
        if (_area is null)
        {
            var aiId = Random.Shared.Next(10_000_000, 2_000_000_000);
            var country = new Country(Guid.NewGuid(), aiId, "Egypt", "مصر");
            var city = new City(Guid.NewGuid(), aiId, country.Id, "Cairo", "القاهرة");
            _area = new Area(Guid.NewGuid(), aiId, city.Id, "Helwan", "حلوان");
            _compound = new Compound(Guid.NewGuid(), aiId, _area.Id, "Garden Residence", "جاردن ريزيدنس");

            await using var context = _fixture.CreateContext();
            context.Countries.Add(country);
            context.Cities.Add(city);
            context.Areas.Add(_area);
            context.Compounds.Add(_compound);
            await context.SaveChangesAsync();
        }

        return new CreatePropertyRequest(
            "TwinHouse",
            "EG",
            Bedrooms: 3,
            Bathrooms: 2,
            FinishingStatus: "LuxuryFinished",
            AmenityCodes: ["SwimmingPool", "Balcony"],
            ViewCodes: ["Sea", "City"],
            TransactionType: "Sale",
            Price: 4_500_000m,
            AreaSqm: 180m,
            Floor: 5,
            Furnished: false,
            ListingStatus: "Available",
            TitleAr: "عنوان",
            TitleEn: "Title",
            DescriptionAr: "وصف",
            DescriptionEn: "Description",
            AreaId: _area.Id,
            CompoundId: _compound!.Id);
    }

    private static UpdatePropertyRequest ToUpdate(CreatePropertyRequest request)
    {
        return new UpdatePropertyRequest(
            request.Type,
            request.CountryCode,
            request.City,
            request.Region,
            request.District,
            request.AddressLine,
            request.PostalCode,
            request.Latitude,
            request.Longitude,
            request.Bedrooms,
            request.Bathrooms,
            request.BuiltDate,
            request.FinishingStatus,
            request.AmenityCodes,
            request.ViewCodes,
            request.TransactionType,
            request.Price,
            request.AreaSqm,
            request.Floor,
            request.Furnished,
            request.ListingStatus,
            request.TitleAr,
            request.TitleEn,
            request.DescriptionAr,
            request.DescriptionEn,
            request.AreaId,
            request.CompoundId);
    }

    private async Task<Property> SeedAsync()
    {
        var now = DateTimeOffset.UtcNow;
        var createdAt = new DateTimeOffset(now.Ticks - (now.Ticks % 10), TimeSpan.Zero).AddMinutes(-10);
        var property = new Property(Guid.NewGuid(), PropertyType.Land, new PropertyAddress("EG"), createdAt);

        await using var context = _fixture.CreateContext();
        context.Properties.Add(property);
        await context.SaveChangesAsync();

        return property;
    }

    private async Task<Property> LoadAsync(Guid id)
    {
        await using var context = _fixture.CreateContext();
        return await context.Properties
            .Include(property => property.Views)
            .AsNoTracking()
            .SingleAsync(property => property.Id == id);
    }
}
