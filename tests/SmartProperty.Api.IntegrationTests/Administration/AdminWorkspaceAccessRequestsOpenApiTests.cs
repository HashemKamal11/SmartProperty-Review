using System.Text.Json;
using SmartProperty.Api.IntegrationTests.Infrastructure;
using Xunit;

namespace SmartProperty.Api.IntegrationTests.Administration;

/// <summary>
/// Reads the document the host actually generates, to confirm the new administration routes are documented as
/// Bearer-protected.
/// </summary>
/// <remarks>
/// The existing operation transformer emits the security requirement for any operation carrying
/// <c>IAuthorizeData</c> and no <c>[AllowAnonymous]</c>. <c>RequirePermissionAttribute</c> derives from
/// <c>AuthorizeAttribute</c> precisely so that it satisfies that rule, which means the transformer needed no change
/// — and this test is what proves the generated output is right rather than assumed.
/// </remarks>
[Collection(ApiPostgreSqlCollection.Name)]
public sealed class AdminWorkspaceAccessRequestsOpenApiTests : IDisposable
{
    private const string BasePath = "/api/admin/workspace-access-requests";

    private readonly SmartPropertyApiFactory _factory;

    public AdminWorkspaceAccessRequestsOpenApiTests(ApiPostgreSqlFixture fixture)
    {
        _factory = new SmartPropertyApiFactory(fixture);
    }

    public void Dispose()
    {
        _factory.Dispose();
    }

    [Theory]
    [InlineData(BasePath, "get")]
    [InlineData($"{BasePath}/{{id}}/approve", "post")]
    [InlineData($"{BasePath}/{{id}}/reject", "post")]
    public async Task TheRouteIsDocumentedWithTheBearerSecurityRequirement(string path, string method)
    {
        using var document = await GetOpenApiDocumentAsync();

        var operation = document.RootElement
            .GetProperty("paths")
            .GetProperty(path)
            .GetProperty(method);

        var scheme = operation
            .GetProperty("security")
            .EnumerateArray()
            .SelectMany(requirement => requirement.EnumerateObject())
            .Select(property => property.Name)
            .ToArray();

        Assert.Contains("Bearer", scheme);
    }

    [Fact]
    public async Task TheDocumentDoesNotPublishThePermissionCode()
    {
        using var client = _factory.CreateClient();

        var json = await client.GetStringAsync("/openapi/v1.json");

        // Permission codes are an internal authorization contract. Nothing in the project's conventions publishes
        // them, and the generated document must not start doing so by accident.
        Assert.DoesNotContain("workspace.access_requests.review", json, StringComparison.OrdinalIgnoreCase);
    }

    private async Task<JsonDocument> GetOpenApiDocumentAsync()
    {
        using var client = _factory.CreateClient();

        // The document endpoint is mapped in Development and Staging, and the test host runs as Development.
        var json = await client.GetStringAsync("/openapi/v1.json");

        return JsonDocument.Parse(json);
    }
}
