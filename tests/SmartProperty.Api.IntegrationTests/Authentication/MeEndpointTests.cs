using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using SmartProperty.Api.Contracts;
using SmartProperty.Api.Contracts.Authentication;
using SmartProperty.Api.Infrastructure.Errors;
using SmartProperty.Api.Infrastructure.Http;
using SmartProperty.Api.IntegrationTests.Infrastructure;
using Xunit;

namespace SmartProperty.Api.IntegrationTests.Authentication;

/// <summary>
/// Regression for the first protected production route, covering both the unauthenticated challenge and the
/// authenticated read against a real user row.
/// </summary>
[Collection(ApiPostgreSqlCollection.Name)]
public sealed class MeEndpointTests : IDisposable
{
    private const string Endpoint = "/api/auth/me";

    private readonly ApiPostgreSqlFixture _fixture;
    private readonly SmartPropertyApiFactory _factory;
    private readonly AuthScenario _scenario;

    public MeEndpointTests(ApiPostgreSqlFixture fixture)
    {
        _fixture = fixture;
        _factory = new SmartPropertyApiFactory(fixture);
        _scenario = new AuthScenario(fixture);
    }

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

    [Fact]
    public async Task ActiveUser_Returns200WithTheCurrentPersistedProfile()
    {
        using var client = _factory.CreateClient();
        var user = await _scenario.RegisterActiveUserAsync(client);

        // The token comes from a real login through the real endpoint, and is validated by the real bearer
        // handler on the way back in. Nothing here installs a test authentication scheme or builds a principal.
        var tokens = await AuthScenario.LoginAsync(client, user.Email);

        using var authenticated = AuthScenario.Authenticate(_factory.CreateClient(), tokens.AccessToken);

        var response = await authenticated.GetAsync(Endpoint);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = (await response.Content.ReadFromJsonAsync<MeResponse>())!;

        await using var context = _fixture.CreateContext();
        var persisted = await context.Users.SingleAsync(candidate => candidate.Id == user.UserId);

        // Every field is compared against the row, not against the request: /me reads the database, and the
        // access token carries nothing but the subject.
        Assert.Equal(persisted.Id, body.UserId);
        Assert.Equal(persisted.Email, body.Email);
        Assert.Equal(persisted.FirstName, body.FirstName);
        Assert.Equal(persisted.LastName, body.LastName);
    }

    [Fact]
    public async Task PreviouslyIssuedToken_Returns403OnceTheAccountIsNoLongerActive()
    {
        using var client = _factory.CreateClient();
        var user = await _scenario.RegisterActiveUserAsync(client);

        var tokens = await AuthScenario.LoginAsync(client, user.Email);

        // The token stays cryptographically valid: it is not expired, revoked, or altered. Only the account
        // status changes, which is what makes this a test of the database re-read rather than of the handler.
        await _scenario.UpdateUserAsync(user.UserId, (candidate, now) => candidate.Suspend(now));

        using var authenticated = AuthScenario.Authenticate(_factory.CreateClient(), tokens.AccessToken);

        var response = await authenticated.GetAsync(Endpoint);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);

        var error = (await response.Content.ReadFromJsonAsync<ApiErrorResponse>())!;
        Assert.Equal("authentication.account_unavailable", error.Code);
        Assert.Equal(StatusCodes.Status403Forbidden, error.Status);
    }

    [Fact]
    public async Task TokenForAUserThatWasNeverPersisted_Returns401()
    {
        // A structurally valid token for a subject that names no row. The handler answers with the same 401 an
        // unauthenticated request gets, so a caller cannot use /me to discover whether a user id is real.
        using var authenticated = _factory.CreateAuthenticatedClient(Guid.NewGuid());

        var response = await authenticated.GetAsync(Endpoint);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);

        var error = (await response.Content.ReadFromJsonAsync<ApiErrorResponse>())!;
        Assert.Equal(ApiErrorCodes.Unauthorized, error.Code);
        Assert.Equal(StatusCodes.Status401Unauthorized, error.Status);
    }
}
