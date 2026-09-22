using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Http;
using SmartProperty.Api.Contracts;
using SmartProperty.Api.Infrastructure.Errors;
using SmartProperty.Api.Infrastructure.Http;
using SmartProperty.Api.IntegrationTests.Infrastructure;
using Xunit;

namespace SmartProperty.Api.IntegrationTests.Authentication;

/// <summary>
/// Regression for the first protected production route. Only the unauthenticated paths are covered here: a
/// successful /me reads a real user row, which belongs to the database-backed tests in STEP 05.7B.
/// </summary>
public sealed class MeEndpointTests : IDisposable
{
    private const string Endpoint = "/api/auth/me";

    private readonly SmartPropertyApiFactory _factory = new();

    public void Dispose()
    {
        _factory.Dispose();
    }

    [Fact]
    public async Task AnonymousRequest_Returns401WithTheStandardUnauthorizedContract()
    {
        using var client = _factory.CreateClient();

        var response = await client.GetAsync(Endpoint);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);

        var error = (await response.Content.ReadFromJsonAsync<ApiErrorResponse>())!;
        Assert.Equal(ApiErrorCodes.Unauthorized, error.Code);
        Assert.Equal(StatusCodes.Status401Unauthorized, error.Status);
        Assert.False(string.IsNullOrWhiteSpace(error.CorrelationId));
    }

    [Fact]
    public async Task AnonymousRequest_ChallengesWithTheBareBearerScheme()
    {
        using var client = _factory.CreateClient();

        var response = await client.GetAsync(Endpoint);

        var challenge = Assert.Single(response.Headers.WwwAuthenticate);
        Assert.Equal(JwtBearerDefaults.AuthenticationScheme, challenge.Scheme);
        Assert.Null(challenge.Parameter);
    }

    [Fact]
    public async Task AnonymousRequest_IsChallengedBeforeAnythingQueriesTheDatabase()
    {
        // The test host points EF Core at an unreachable server, so a query before the challenge would surface
        // as a connection failure — a 500, or a much slower response — rather than the 401 asserted here.
        using var client = _factory.CreateClient();

        var response = await client.GetAsync(Endpoint);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task AnonymousRequest_EchoesTheRequestCorrelationId()
    {
        const string correlationId = "tests-me-401";
        using var client = _factory.CreateClient();

        using var request = new HttpRequestMessage(HttpMethod.Get, Endpoint);
        request.Headers.Add(CorrelationIdFeature.HeaderName, correlationId);

        var response = await client.SendAsync(request);

        Assert.Equal(correlationId, Assert.Single(response.Headers.GetValues(CorrelationIdFeature.HeaderName)));

        var error = (await response.Content.ReadFromJsonAsync<ApiErrorResponse>())!;
        Assert.Equal(correlationId, error.CorrelationId);
    }
}
