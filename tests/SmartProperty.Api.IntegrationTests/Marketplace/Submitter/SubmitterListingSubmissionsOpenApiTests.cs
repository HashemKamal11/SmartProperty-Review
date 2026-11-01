using System.Text.Json;
using SmartProperty.Api.IntegrationTests.Infrastructure;
using Xunit;

namespace SmartProperty.Api.IntegrationTests.Marketplace.Submitter;

/// <summary>
/// What the generated OpenAPI document says about the submitter reads when the controller is mounted, and that the
/// production composition documents nothing of them.
/// </summary>
/// <remarks>
/// Reads the document the host actually generates, with a test-only stub in place of the read repository. No database
/// is involved.
/// </remarks>
public sealed class SubmitterListingSubmissionsOpenApiTests
{
    private const string ListPath = "/api/marketplace/me/listing-submissions";
    private const string DetailPath = "/api/marketplace/me/listing-submissions/{submissionId}";

    private static readonly string[] PrivateTerms =
    [
        "propertyId", "legalParty", "linkedUser", "actingUser", "userId", "submitterId", "workspace", "evidence",
        "sha256", "digest", "assessment", "snapshot", "reviewer", "ownerKind", "grant", "verification",
    ];

    [Fact]
    public async Task BothReadsAreDocumentedWithTheBearerSecurityRequirement()
    {
        using var document = await LoadDocumentAsync(mount: true);
        var paths = document.RootElement.GetProperty("paths");

        AssertBearerSecurity(paths.GetProperty(ListPath).GetProperty("get"));
        AssertBearerSecurity(paths.GetProperty(DetailPath).GetProperty("get"));
    }

    [Fact]
    public async Task OnlyGetOperationsAreDocumentedForTheSubmitterPaths()
    {
        using var document = await LoadDocumentAsync(mount: true);
        var paths = document.RootElement.GetProperty("paths");

        Assert.Equal(new[] { "get" }, OperationNames(paths.GetProperty(ListPath)));
        Assert.Equal(new[] { "get" }, OperationNames(paths.GetProperty(DetailPath)));
    }

    [Fact]
    public async Task TheListDocumentsExactlyTheApprovedQueryParametersAndNoIdentityParameter()
    {
        using var document = await LoadDocumentAsync(mount: true);
        var list = document.RootElement.GetProperty("paths").GetProperty(ListPath).GetProperty("get");

        var parameters = list.GetProperty("parameters").EnumerateArray().ToArray();
        Assert.Equal(
            new[] { "page", "pageSize", "purpose", "status" },
            parameters.Select(parameter => parameter.GetProperty("name").GetString()!).Order(StringComparer.Ordinal).ToArray());
        Assert.All(parameters, parameter => Assert.Equal("query", parameter.GetProperty("in").GetString()));
        AssertResponses(list, present: ["200", "400", "401", "422"], absent: ["403", "404"]);
    }

    [Fact]
    public async Task TheDetailDocumentsTheRequiredGuidPathParameterAndItsResponses()
    {
        using var document = await LoadDocumentAsync(mount: true);
        var detail = document.RootElement.GetProperty("paths").GetProperty(DetailPath).GetProperty("get");

        var parameter = Assert.Single(detail.GetProperty("parameters").EnumerateArray());
        Assert.Equal("submissionId", parameter.GetProperty("name").GetString());
        Assert.Equal("path", parameter.GetProperty("in").GetString());
        Assert.True(parameter.GetProperty("required").GetBoolean());
        Assert.Equal("string", parameter.GetProperty("schema").GetProperty("type").GetString());
        Assert.Equal("uuid", parameter.GetProperty("schema").GetProperty("format").GetString());
        AssertResponses(detail, present: ["200", "401", "404"], absent: ["400", "403", "422"]);
    }

    [Fact]
    public async Task TheResponsesUseTheSubmitterContractsAndThePageEnvelope()
    {
        using var document = await LoadDocumentAsync(mount: true);
        var schemas = document.RootElement.GetProperty("components").GetProperty("schemas");
        var names = schemas.EnumerateObject().Select(property => property.Name).ToArray();

        Assert.Contains("SubmitterSubmissionSummaryResponse", names);
        Assert.Contains("SubmitterSubmissionDetailResponse", names);
        Assert.Contains("SubmitterSubmissionDecisionResponse", names);
        Assert.Contains(
            names,
            name => name.StartsWith("PagedResponse", StringComparison.Ordinal)
                && name.Contains("SubmitterSubmissionSummaryResponse", StringComparison.Ordinal));

        Assert.Equal(
            new[]
            {
                "createdAt", "isCurrentlyPublished", "listingId", "location", "price", "propertyType", "purpose",
                "representation", "status", "submissionId", "submittedAt", "title", "updatedAt", "version",
            },
            PropertyNames(schemas.GetProperty("SubmitterSubmissionSummaryResponse")));
        Assert.Equal(
            new[]
            {
                "createdAt", "decision", "description", "isCurrentlyPublished", "listingId", "location", "price",
                "propertyType", "purpose", "representation", "status", "submissionId", "submittedAt", "title",
                "updatedAt", "version",
            },
            PropertyNames(schemas.GetProperty("SubmitterSubmissionDetailResponse")));
        Assert.Equal(
            new[] { "approved", "decidedAt", "reason" },
            PropertyNames(schemas.GetProperty("SubmitterSubmissionDecisionResponse")));
    }

    [Fact]
    public async Task NoSubmitterOperationOrSchemaMentionsAPrivateFieldName()
    {
        using var document = await LoadDocumentAsync(mount: true);
        var root = document.RootElement;
        var schemas = root.GetProperty("components").GetProperty("schemas");

        var published = new List<string>
        {
            root.GetProperty("paths").GetProperty(ListPath).GetRawText(),
            root.GetProperty("paths").GetProperty(DetailPath).GetRawText(),
        };
        published.AddRange(schemas.EnumerateObject()
            .Where(schema => schema.Name.Contains("SubmitterSubmission", StringComparison.Ordinal))
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
    public async Task TheProductionCompositionDocumentsNoSubmitterPathOrSchema()
    {
        using var document = await LoadDocumentAsync(mount: false);
        var root = document.RootElement;
        var paths = root.GetProperty("paths").EnumerateObject().Select(property => property.Name).ToArray();
        var schemas = root.GetProperty("components").GetProperty("schemas").EnumerateObject().Select(property => property.Name).ToArray();

        // The document is real: the public marketplace is in it.
        Assert.Contains(paths, path => path.StartsWith("/api/public/marketplace/listings", StringComparison.Ordinal));
        Assert.DoesNotContain(paths, path => path.Contains("marketplace/me", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(schemas, name => name.Contains("SubmitterSubmission", StringComparison.Ordinal));
    }

    private static async Task<JsonDocument> LoadDocumentAsync(bool mount)
    {
        using var host = new SubmitterApiTestHost(new StubSubmitterListingSubmissionReadRepository(), mount);
        using var client = host.CreateClient();

        return JsonDocument.Parse(await client.GetStringAsync("/openapi/v1.json"));
    }

    private static string[] PropertyNames(JsonElement schema)
    {
        return schema.GetProperty("properties")
            .EnumerateObject()
            .Select(property => property.Name)
            .Order(StringComparer.Ordinal)
            .ToArray();
    }

    private static string[] OperationNames(JsonElement pathItem)
    {
        return pathItem.EnumerateObject()
            .Select(property => property.Name)
            .Order(StringComparer.Ordinal)
            .ToArray();
    }

    private static void AssertBearerSecurity(JsonElement operation)
    {
        Assert.Contains(
            operation.GetProperty("security").EnumerateArray().SelectMany(value => value.EnumerateObject()),
            value => value.Name == "Bearer");
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
