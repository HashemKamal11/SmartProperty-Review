using System.Text.Json;
using SmartProperty.Api.IntegrationTests.Infrastructure;
using Xunit;

namespace SmartProperty.Api.IntegrationTests.Authentication;

[Collection(ApiPostgreSqlCollection.Name)]
public sealed class AuthContextOpenApiTests(ApiPostgreSqlFixture fixture)
{
    [Fact]
    public async Task DocumentsBearerSuccessAndAccountStateFailuresWithoutDeferredFields()
    {
        using var factory = new SmartPropertyApiFactory(fixture);
        using var client = factory.CreateClient();
        using var document = JsonDocument.Parse(await client.GetStringAsync("/openapi/v1.json"));

        var operation = document.RootElement
            .GetProperty("paths")
            .GetProperty("/api/auth/context")
            .GetProperty("get");

        Assert.Contains(
            operation.GetProperty("security").EnumerateArray().SelectMany(value => value.EnumerateObject()),
            value => value.Name == "Bearer");

        var responses = operation.GetProperty("responses");
        Assert.True(responses.TryGetProperty("200", out _));
        Assert.True(responses.TryGetProperty("401", out _));
        Assert.True(responses.TryGetProperty("403", out _));

        var schemaReference = responses
            .GetProperty("200")
            .GetProperty("content")
            .GetProperty("application/json")
            .GetProperty("schema")
            .GetProperty("$ref")
            .GetString();
        Assert.EndsWith("/AuthContextResponse", schemaReference, StringComparison.Ordinal);

        var schema = document.RootElement
            .GetProperty("components")
            .GetProperty("schemas")
            .GetProperty("AuthContextResponse")
            .GetRawText();
        Assert.DoesNotContain("workspaceType", schema, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("currentWorkspace", schema, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("activityMode", schema, StringComparison.OrdinalIgnoreCase);
    }
}
