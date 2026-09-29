using System.Net;
using Microsoft.Extensions.Hosting;
using Microsoft.Net.Http.Headers;
using SmartProperty.Api.IntegrationTests.Infrastructure;
using Xunit;

namespace SmartProperty.Api.IntegrationTests.Configuration;

[Collection(ApiPostgreSqlCollection.Name)]
public sealed class CorsPolicyTests(ApiPostgreSqlFixture fixture)
{
    private const string AllowedOrigin = "https://frontend.example.test";
    private const string DeniedOrigin = "https://evil.example.test";

    [Fact]
    public async Task ConfiguredOrigin_ReceivesItsExactAllowOriginWithoutCredentialsOrWildcard()
    {
        using var factory = CreateFactory(AllowedOrigin);
        using var client = factory.CreateClient();
        using var request = CreateGetRequest(AllowedOrigin);

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(AllowedOrigin, Assert.Single(response.Headers.GetValues(HeaderNames.AccessControlAllowOrigin)));
        Assert.NotEqual("*", Assert.Single(response.Headers.GetValues(HeaderNames.AccessControlAllowOrigin)));
        Assert.False(response.Headers.Contains(HeaderNames.AccessControlAllowCredentials));
    }

    [Fact]
    public async Task UnconfiguredOrigin_ReceivesNoAllowOriginHeader()
    {
        using var factory = CreateFactory(AllowedOrigin);
        using var client = factory.CreateClient();
        using var request = CreateGetRequest(DeniedOrigin);

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.False(response.Headers.Contains(HeaderNames.AccessControlAllowOrigin));
    }

    [Fact]
    public async Task EmptyAllowedOrigins_FailsClosedForCrossOriginRequests()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();
        using var request = CreateGetRequest(AllowedOrigin);

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.False(response.Headers.Contains(HeaderNames.AccessControlAllowOrigin));
    }

    [Fact]
    public async Task RequestWithoutOrigin_ContinuesNormallyWhenNoOriginsAreConfigured()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/health/live");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.False(response.Headers.Contains(HeaderNames.AccessControlAllowOrigin));
    }

    [Fact]
    public async Task ConfiguredOrigin_PreflightAllowsAuthorizationAndContentTypeWithoutCredentials()
    {
        using var factory = CreateFactory(AllowedOrigin);
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Options, "/api/admin/workspace-access-requests");
        request.Headers.TryAddWithoutValidation(HeaderNames.Origin, AllowedOrigin);
        request.Headers.TryAddWithoutValidation(HeaderNames.AccessControlRequestMethod, "GET");
        request.Headers.TryAddWithoutValidation(
            HeaderNames.AccessControlRequestHeaders,
            "Authorization, Content-Type");

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(AllowedOrigin, Assert.Single(response.Headers.GetValues(HeaderNames.AccessControlAllowOrigin)));
        var allowedHeaders = response.Headers
            .GetValues(HeaderNames.AccessControlAllowHeaders)
            .SelectMany(value => value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            .ToArray();
        Assert.Contains(allowedHeaders, header => header.Equals("Authorization", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(allowedHeaders, header => header.Equals("Content-Type", StringComparison.OrdinalIgnoreCase));
        Assert.False(response.Headers.Contains(HeaderNames.AccessControlAllowCredentials));
    }

    [Fact]
    public async Task MalformedConfiguredOrigin_StopsTheHost()
    {
        using var factory = CreateFactory("https://frontend.example.test/path");

        var failure = await Assert.ThrowsAnyAsync<Exception>(async () =>
        {
            using var client = factory.CreateClient();
            using var response = await client.GetAsync("/health/live");
        });

        Assert.Contains("Cors", Describe(failure), StringComparison.Ordinal);
    }

    private SmartPropertyApiFactory CreateFactory(params string[] allowedOrigins)
    {
        var configuration = allowedOrigins
            .Select((origin, index) =>
                new KeyValuePair<string, string?>($"Cors:AllowedOrigins:{index}", origin))
            .ToArray();

        return new SmartPropertyApiFactory(
            fixture,
            configuration: configuration,
            environment: Environments.Staging);
    }

    private static HttpRequestMessage CreateGetRequest(string origin)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/health/live");
        request.Headers.TryAddWithoutValidation(HeaderNames.Origin, origin);

        return request;
    }

    private static string Describe(Exception exception)
    {
        var messages = new List<string>();

        for (var current = exception; current is not null; current = current.InnerException)
        {
            messages.Add(current.Message);
        }

        return string.Join(Environment.NewLine, messages);
    }
}
