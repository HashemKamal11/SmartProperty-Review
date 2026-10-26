using System.Text.Json;
using SmartProperty.Api.IntegrationTests.Infrastructure;
using Xunit;

namespace SmartProperty.Api.IntegrationTests.Marketplace.Public;

/// <summary>
/// What the generated OpenAPI document says about the public marketplace: both reads are documented, neither asks
/// for a token, and no private field name appears in them or in their schemas.
/// </summary>
/// <remarks>
/// Reads the document the host actually generates, with a test-only stub in place of the read repository. No
/// database is involved.
/// </remarks>
public sealed class PublicListingOpenApiTests
{
    private const string ListPath = "/api/public/marketplace/listings";
    private const string DetailPath = "/api/public/marketplace/listings/{listingId}";

    private static readonly string[] PrivateTerms =
    [
        "propertyId", "submission", "evidence", "owner", "workspace", "reviewer", "review", "addressLine",
        "postalCode", "latitude", "longitude", "permission",
    ];

    [Fact]
    public async Task BothPublicReadsAreDocumentedWithNoSecurityRequirement()
    {
        using var document = await LoadDocumentAsync();
        var paths = document.RootElement.GetProperty("paths");

        var list = paths.GetProperty(ListPath).GetProperty("get");
        var detail = paths.GetProperty(DetailPath).GetProperty("get");

        Assert.False(list.TryGetProperty("security", out _), "The public search must not require a token.");
        Assert.False(detail.TryGetProperty("security", out _), "The public detail must not require a token.");
    }

    [Fact]
    public async Task OnlyGetOperationsAreDocumentedForThePublicPaths()
    {
        using var document = await LoadDocumentAsync();
        var paths = document.RootElement.GetProperty("paths");

        Assert.Equal(new[] { "get" }, OperationNames(paths.GetProperty(ListPath)));
        Assert.Equal(new[] { "get" }, OperationNames(paths.GetProperty(DetailPath)));
    }

    [Fact]
    public async Task TheSearchDocumentsExactlyTheApprovedParametersAndResponses()
    {
        using var document = await LoadDocumentAsync();
        var list = document.RootElement.GetProperty("paths").GetProperty(ListPath).GetProperty("get");

        Assert.Equal(
            new[]
            {
                "city", "countryCode", "currency", "district", "maxPrice", "minPrice", "page", "pageSize",
                "propertyType", "purpose", "region", "rentPeriod", "sort",
            },
            list.GetProperty("parameters")
                .EnumerateArray()
                .Select(parameter => parameter.GetProperty("name").GetString()!)
                .Order(StringComparer.Ordinal)
                .ToArray());
        Assert.All(
            list.GetProperty("parameters").EnumerateArray(),
            parameter => Assert.Equal("query", parameter.GetProperty("in").GetString()));

        AssertResponses(list, present: ["200", "400", "422"], absent: ["401", "403", "404"]);
    }

    [Fact]
    public async Task TheDetailDocumentsTheRequiredGuidPathParameterAndItsResponses()
    {
        using var document = await LoadDocumentAsync();
        var detail = document.RootElement.GetProperty("paths").GetProperty(DetailPath).GetProperty("get");

        var parameter = Assert.Single(detail.GetProperty("parameters").EnumerateArray());
        Assert.Equal("listingId", parameter.GetProperty("name").GetString());
        Assert.Equal("path", parameter.GetProperty("in").GetString());
        Assert.True(parameter.GetProperty("required").GetBoolean());

        AssertResponses(detail, present: ["200", "404"], absent: ["400", "401", "403", "422"]);
    }

    [Fact]
    public async Task TheResponsesUseThePublicDtosAndThePageEnvelope()
    {
        using var document = await LoadDocumentAsync();
        var schemas = document.RootElement.GetProperty("components").GetProperty("schemas");
        var names = schemas.EnumerateObject().Select(property => property.Name).ToArray();

        Assert.Contains("PublicListingSummaryResponse", names);
        Assert.Contains("PublicListingDetailResponse", names);
        Assert.Contains("PublicMoneyResponse", names);
        Assert.Contains("PublicLocationResponse", names);
        Assert.Contains(names, name => name.StartsWith("PagedResponse", StringComparison.Ordinal) && name.Contains("PublicListingSummaryResponse", StringComparison.Ordinal));

        var money = schemas.GetProperty("PublicMoneyResponse").GetProperty("properties");
        Assert.Equal(
            new[] { "amount", "currency", "rentPeriod" },
            money.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public async Task NoPublicOperationOrSchemaMentionsAPrivateFieldName()
    {
        using var document = await LoadDocumentAsync();
        var root = document.RootElement;
        var schemas = root.GetProperty("components").GetProperty("schemas");

        var published = new List<string>
        {
            root.GetProperty("paths").GetProperty(ListPath).GetRawText(),
            root.GetProperty("paths").GetProperty(DetailPath).GetRawText(),
        };
        published.AddRange(schemas.EnumerateObject()
            .Where(schema => schema.Name.Contains("PublicListing", StringComparison.Ordinal)
                || schema.Name.Contains("PublicMoney", StringComparison.Ordinal)
                || schema.Name.Contains("PublicLocation", StringComparison.Ordinal))
            .Select(schema => schema.Value.GetRawText()));

        Assert.True(published.Count > 2);

        foreach (var text in published)
        {
            foreach (var term in PrivateTerms)
            {
                Assert.DoesNotContain(term, text, StringComparison.OrdinalIgnoreCase);
            }
        }
    }

    [Fact]
    public async Task AProtectedOperationStillDeclaresBearerSoTheAbsenceHereIsMeaningful()
    {
        using var document = await LoadDocumentAsync();
        var properties = document.RootElement.GetProperty("paths").GetProperty("/api/properties").GetProperty("get");

        Assert.Contains(
            properties.GetProperty("security").EnumerateArray().SelectMany(value => value.EnumerateObject()),
            value => value.Name == "Bearer");
    }

    private static async Task<JsonDocument> LoadDocumentAsync()
    {
        using var host = new PublicApiTestHost(new StubPublicListingReadRepository());
        using var client = host.CreateClient();

        return JsonDocument.Parse(await client.GetStringAsync("/openapi/v1.json"));
    }

    private static string[] OperationNames(JsonElement pathItem)
    {
        return pathItem.EnumerateObject()
            .Select(property => property.Name)
            .Order(StringComparer.Ordinal)
            .ToArray();
    }

    private static void AssertResponses(JsonElement operation, string[] present, string[] absent)
    {
        var responses = operation.GetProperty("responses");

        foreach (var status in present)
        {
            Assert.True(responses.TryGetProperty(status, out _), $"Response {status} was not documented.");
        }

        foreach (var status in absent)
        {
            Assert.False(responses.TryGetProperty(status, out _), $"Response {status} must not be documented.");
        }
    }
}
