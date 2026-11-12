using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using SmartProperty.Api.Contracts.PropertyRegistry;
using SmartProperty.Api.IntegrationTests.Infrastructure;
using SmartProperty.Application.Authorization;
using Xunit;

namespace SmartProperty.Api.IntegrationTests.Localization;

/// <summary>
/// The request chooses the language (<c>?lang=</c> first, then <c>Accept-Language</c>, English by default) and the real
/// host answers with <c>display*</c> fields and <c>Content-Language</c> in it.
/// </summary>
[Collection(ApiPostgreSqlCollection.Name)]
public sealed class DisplayLanguageEndpointTests : IDisposable
{
    private readonly ApiPostgreSqlFixture _fixture;
    private readonly SmartPropertyApiFactory _factory;

    public DisplayLanguageEndpointTests(ApiPostgreSqlFixture fixture)
    {
        _fixture = fixture;
        _factory = new SmartPropertyApiFactory(fixture);
    }

    public void Dispose()
    {
        _factory.Dispose();
    }

    [Theory]
    [InlineData("ar", "شرفة", "ar")]
    [InlineData("ar-EG,ar;q=0.9,en;q=0.8", "شرفة", "ar")]
    [InlineData("en-US", "Balcony", "en")]
    [InlineData("fr", "Balcony", "en")]
    [InlineData(null, "Balcony", "en")]
    public async Task AcceptLanguageChoosesTheDisplayLanguage(string? acceptLanguage, string expected, string contentLanguage)
    {
        using var client = _factory.CreateAuthenticatedClient(Guid.NewGuid());
        if (acceptLanguage is not null)
        {
            client.DefaultRequestHeaders.Add("Accept-Language", acceptLanguage);
        }

        using var response = await client.GetAsync("/api/amenities");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var first = (await response.Content.ReadFromJsonAsync<JsonElement>())[0];
        Assert.Equal(expected, first.GetProperty("displayName").GetString());
        Assert.Equal("شرفة", first.GetProperty("nameAr").GetString());
        Assert.Equal("Balcony", first.GetProperty("nameEn").GetString());
        Assert.Equal([contentLanguage], response.Content.Headers.ContentLanguage);
        Assert.Contains("Accept-Language", response.Headers.Vary);
    }

    [Fact]
    public async Task TheLangQueryOverridesAcceptLanguage()
    {
        using var client = _factory.CreateAuthenticatedClient(Guid.NewGuid());
        client.DefaultRequestHeaders.Add("Accept-Language", "en");

        var amenities = await client.GetFromJsonAsync<JsonElement>("/api/amenities?lang=ar");

        Assert.Equal("شرفة", amenities[0].GetProperty("displayName").GetString());
    }

    [Fact]
    public async Task ErrorBodiesCarryTheMessageInTheRequestedLanguage()
    {
        using var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("Accept-Language", "ar");

        using var response = await client.GetAsync("/api/amenities");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Authentication is required.", body.GetProperty("message").GetString());
        Assert.Equal("المصادقة مطلوبة.", body.GetProperty("displayMessage").GetString());
    }

    [Fact]
    public async Task ValidationErrorsAreTranslatedWithTheirValues()
    {
        using var client = AuthorizedClient();

        using var response = await client.PostAsJsonAsync(
            "/api/properties?lang=ar",
            new CreatePropertyRequest("Apartment", TestPlaces.Seed(_fixture).AreaId, AmenityCodes: ["Helipad"]));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var display = body.GetProperty("displayMessage").GetString()!;
        Assert.Contains("Helipad", display);
        Assert.NotEqual(body.GetProperty("message").GetString(), display);
    }

    [Fact]
    public async Task ClosedListValuesAndPlacesAreDisplayedInArabic()
    {
        using var client = AuthorizedClient();
        client.DefaultRequestHeaders.Add("Accept-Language", "ar");

        using var response = await client.PostAsJsonAsync(
            "/api/properties",
            new CreatePropertyRequest("Apartment", TestPlaces.Seed(_fixture).AreaId, AmenityCodes: ["Balcony"]));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Apartment", body.GetProperty("type").GetString());
        Assert.Equal("شقة", body.GetProperty("displayType").GetString());
        Assert.Equal("Active", body.GetProperty("status").GetString());
        Assert.Equal("نشط", body.GetProperty("displayStatus").GetString());
        Assert.Equal("شرفة", body.GetProperty("amenities")[0].GetProperty("displayName").GetString());
    }

    [Fact]
    public async Task TheOpenApiDocumentDescribesTheDisplayFields()
    {
        using var client = _factory.CreateClient();

        var document = await client.GetFromJsonAsync<JsonElement>("/openapi/v1.json");

        var amenity = document.GetProperty("components").GetProperty("schemas").GetProperty("AmenityResponse").GetProperty("properties");
        Assert.True(amenity.TryGetProperty("displayName", out _));
        var error = document.GetProperty("components").GetProperty("schemas").GetProperty("ApiErrorResponse").GetProperty("properties");
        Assert.True(error.TryGetProperty("displayMessage", out _));

        var parameters = document.GetProperty("paths").GetProperty("/api/amenities").GetProperty("get").GetProperty("parameters");
        var lang = parameters.EnumerateArray().Single(parameter => parameter.GetProperty("name").GetString() == "lang");
        Assert.Equal("query", lang.GetProperty("in").GetString());
        Assert.Equal(["ar", "en"], lang.GetProperty("schema").GetProperty("enum").EnumerateArray().Select(value => value.GetString()));
    }

    private HttpClient AuthorizedClient()
    {
        _factory.PermissionChecker.AllowedPermissionCodes.Add(PermissionCodes.PropertyCreate);
        _factory.PermissionChecker.AllowedPermissionCodes.Add(PermissionCodes.PropertyRead);
        _factory.PermissionChecker.AllowedPermissionCodes.Add(PermissionCodes.PropertyUpdate);
        return _factory.CreateAuthenticatedClient(TestUsers.Seed(_fixture));
    }
}
