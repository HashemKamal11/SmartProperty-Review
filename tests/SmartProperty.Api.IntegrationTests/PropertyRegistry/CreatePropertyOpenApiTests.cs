using System.Text.Json;
using SmartProperty.Api.IntegrationTests.Infrastructure;
using SmartProperty.Application.Authorization;
using Xunit;

namespace SmartProperty.Api.IntegrationTests.PropertyRegistry;

[Collection(ApiPostgreSqlCollection.Name)]
public sealed class CreatePropertyOpenApiTests(ApiPostgreSqlFixture fixture)
{
    [Fact]
    public async Task OpenApiDocumentsContractSecurityAndExpectedResponses()
    {
        using var factory = new SmartPropertyApiFactory(fixture);
        using var client = factory.CreateClient();
        using var document = JsonDocument.Parse(await client.GetStringAsync("/openapi/v1.json"));

        var operation = document.RootElement
            .GetProperty("paths")
            .GetProperty("/api/properties")
            .GetProperty("post");

        Assert.True(operation.GetProperty("requestBody").GetProperty("required").GetBoolean());
        Assert.Contains(
            operation.GetProperty("security").EnumerateArray().SelectMany(value => value.EnumerateObject()),
            value => value.Name == "Bearer");

        var responses = operation.GetProperty("responses");
        Assert.True(responses.TryGetProperty("201", out _));
        Assert.True(responses.TryGetProperty("400", out _));
        Assert.True(responses.TryGetProperty("401", out _));
        Assert.True(responses.TryGetProperty("403", out _));
        Assert.True(responses.TryGetProperty("422", out _));

        var json = document.RootElement.GetRawText();
        Assert.Contains("CreatePropertyRequest", json, StringComparison.Ordinal);
        Assert.Contains("CreatePropertyResponse", json, StringComparison.Ordinal);
        Assert.DoesNotContain(PermissionCodes.PropertyCreate, json, StringComparison.OrdinalIgnoreCase);
    }
}
