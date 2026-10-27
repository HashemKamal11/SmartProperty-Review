using System.Text.Json;
using SmartProperty.Api.IntegrationTests.Infrastructure;
using Xunit;

namespace SmartProperty.Api.IntegrationTests.Marketplace.Admin;

/// <summary>
/// What the generated OpenAPI document says about the Platform Admin review reads when the controller is mounted, and
/// that the production composition documents nothing of them.
/// </summary>
/// <remarks>
/// Reads the document the host actually generates, with a test-only stub in place of the read repository. No database
/// is involved.
/// </remarks>
public sealed class AdminListingSubmissionReviewOpenApiTests
{
    private const string ListPath = "/api/admin/marketplace/listing-submissions";
    private const string DetailPath = "/api/admin/marketplace/listing-submissions/{submissionId}";

    // Matched against names and published operation text. A reviewer-only field such as propertyId is allowed in the
    // detail, so this list is the data the reviewer contract must never carry.
    private static readonly string[] PrivateTerms =
    [
        "legalParty", "linkedUser", "declaredOwner", "actingUser", "submitterUser", "userId", "reviewerUser",
        "workspace", "sha256", "digest", "assessment", "snapshot", "independence", "affiliat", "concurrency", "storage",
    ];

    private static readonly string[] ReviewSchemas =
    [
        "ReviewQueueItemResponse", "ReviewSubmissionDetailResponse", "ReviewDecisionResponse",
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
    public async Task OnlyGetOperationsAreDocumentedForTheReviewPaths()
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
        AssertResponses(list, present: ["200", "400", "401", "403", "422"], absent: ["404"]);
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
        AssertResponses(detail, present: ["200", "401", "403", "404"], absent: ["400", "422"]);
    }

    [Fact]
    public async Task TheResponsesUseTheReviewerContractsAndThePageEnvelope()
    {
        using var document = await LoadDocumentAsync(mount: true);
        var schemas = document.RootElement.GetProperty("components").GetProperty("schemas");
        var names = schemas.EnumerateObject().Select(property => property.Name).ToArray();

        foreach (var schema in ReviewSchemas)
        {
            Assert.Contains(schema, names);
        }

        Assert.Contains(
            names,
            name => name.StartsWith("PagedResponse", StringComparison.Ordinal)
                && name.Contains("ReviewQueueItemResponse", StringComparison.Ordinal));

        Assert.Equal(
            new[]
            {
                "allEvidenceClean", "evidenceCount", "listingId", "location", "ownerKind", "price", "propertyType",
                "purpose", "representation", "status", "submissionId", "submittedAt", "title", "version",
            },
            PropertyNames(schemas.GetProperty("ReviewQueueItemResponse")));
        Assert.Equal(
            new[]
            {
                "allEvidenceClean", "authorityValidFrom", "authorityValidUntil", "createdAt", "decision", "description",
                "evidenceCount", "isCurrentlyPublished", "listingId", "location", "ownerKind", "predecessorSubmissionId",
                "price", "propertyId", "propertyType", "purpose", "representation", "status", "submissionId",
                "submittedAt", "title", "updatedAt", "version",
            },
            PropertyNames(schemas.GetProperty("ReviewSubmissionDetailResponse")));
        Assert.Equal(
            new[] { "approved", "decidedAt", "reason" },
            PropertyNames(schemas.GetProperty("ReviewDecisionResponse")));
    }

    [Fact]
    public async Task NoReviewOperationOrSchemaMentionsAPrivateFieldName()
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
            .Where(schema => ReviewSchemas.Contains(schema.Name)
                || (schema.Name.StartsWith("PagedResponse", StringComparison.Ordinal)
                    && schema.Name.Contains("ReviewQueueItemResponse", StringComparison.Ordinal)))
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
    public async Task TheProductionCompositionDocumentsNoReviewPathOrSchema()
    {
        using var document = await LoadDocumentAsync(mount: false);
        var root = document.RootElement;
        var paths = root.GetProperty("paths").EnumerateObject().Select(property => property.Name).ToArray();
        var schemas = root.GetProperty("components").GetProperty("schemas").EnumerateObject().Select(property => property.Name).ToArray();

        // The document is real: the public marketplace is in it.
        Assert.Contains(paths, path => path.StartsWith("/api/public/marketplace/listings", StringComparison.Ordinal));
        Assert.DoesNotContain(paths, path => path.Contains("admin/marketplace", StringComparison.OrdinalIgnoreCase));

        foreach (var schema in ReviewSchemas)
        {
            Assert.DoesNotContain(schema, schemas);
        }

        Assert.DoesNotContain(schemas, name => name.Contains("ReviewQueueItemResponse", StringComparison.Ordinal));
    }

    private static async Task<JsonDocument> LoadDocumentAsync(bool mount)
    {
        using var host = new AdminReviewApiTestHost(new StubReviewerListingSubmissionReadRepository(), mount);
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
