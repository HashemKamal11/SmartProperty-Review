using System.Net;
using System.Net.Http.Json;
using SmartProperty.Api.Contracts;
using SmartProperty.Api.Contracts.Authentication;
using SmartProperty.Api.IntegrationTests.Infrastructure;
using Xunit;

namespace SmartProperty.Api.IntegrationTests.Authentication;

/// <summary>
/// Regression coverage for the abuse protections on the anonymous endpoints and the uniform response headers.
/// </summary>
[Collection(ApiPostgreSqlCollection.Name)]
public sealed class AbuseProtectionTests(ApiPostgreSqlFixture fixture)
{
    private const string Login = "/api/auth/login";

    private static readonly KeyValuePair<string, string?>[] LimitedConfiguration =
    [
        new("RateLimiting:Enabled", "true"),
        new("RateLimiting:AuthPermitLimit", "3"),
        new("RateLimiting:WindowSeconds", "60")
    ];

    [Fact]
    public async Task RepeatedLoginAttemptsFromOneClient_AreThrottledWith429AndRetryAfter()
    {
        using var factory = new SmartPropertyApiFactory(fixture, configuration: LimitedConfiguration);
        using var client = factory.CreateClient();

        for (var attempt = 0; attempt < 3; attempt++)
        {
            using var allowed = await client.PostAsJsonAsync(Login, new LoginRequest($"nobody{attempt}@example.com", "wrong-password"));
            Assert.Equal(HttpStatusCode.Unauthorized, allowed.StatusCode);
        }

        using var throttled = await client.PostAsJsonAsync(Login, new LoginRequest("nobody@example.com", "wrong-password"));

        Assert.Equal(HttpStatusCode.TooManyRequests, throttled.StatusCode);
        Assert.True(throttled.Headers.Contains("Retry-After"));

        var error = (await throttled.Content.ReadFromJsonAsync<ApiErrorResponse>())!;
        Assert.Equal("request.too_many_requests", error.Code);
    }

    [Fact]
    public async Task ThrottlingAppliesToTheCodeSendingEndpointsToo()
    {
        using var factory = new SmartPropertyApiFactory(fixture, configuration: LimitedConfiguration);
        using var client = factory.CreateClient();

        HttpStatusCode last = default;

        for (var attempt = 0; attempt < 4; attempt++)
        {
            using var response = await client.PostAsJsonAsync(
                "/api/auth/forgot-password",
                new ForgotPasswordRequest("nobody@example.com"));
            last = response.StatusCode;
        }

        Assert.Equal(HttpStatusCode.TooManyRequests, last);
    }

    [Fact]
    public async Task EveryResponseCarriesTheSecurityHeaders()
    {
        using var factory = new SmartPropertyApiFactory(fixture);
        using var client = factory.CreateClient();

        using var response = await client.PostAsJsonAsync(Login, new LoginRequest("nobody@example.com", "wrong-password"));

        Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Equal("DENY", response.Headers.GetValues("X-Frame-Options").Single());
        Assert.Equal("no-referrer", response.Headers.GetValues("Referrer-Policy").Single());
        Assert.Contains("frame-ancestors 'none'", response.Headers.GetValues("Content-Security-Policy").Single());
    }

    [Fact]
    public async Task AuthResponses_AreNeverCacheable()
    {
        using var factory = new SmartPropertyApiFactory(fixture);
        using var client = factory.CreateClient();

        using var response = await client.PostAsJsonAsync(Login, new LoginRequest("nobody@example.com", "wrong-password"));

        var cacheControl = string.Join(",", response.Headers.GetValues("Cache-Control"));
        Assert.Contains("no-store", cacheControl);
    }
}
