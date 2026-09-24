using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using SmartProperty.Api.Contracts;
using SmartProperty.Api.Contracts.Authentication;
using SmartProperty.Api.IntegrationTests.Infrastructure;
using Xunit;

namespace SmartProperty.Api.IntegrationTests.Authentication;

/// <summary>
/// End-to-end coverage for POST /api/auth/login against the real host and a real database.
/// </summary>
/// <remarks>
/// The two failure shapes are deliberately different and both are pinned here: not knowing the password is a 401
/// that never says which half was wrong, while a correct password on an account that may not sign in is a 403
/// that never says which status the account is in.
/// </remarks>
[Collection(ApiPostgreSqlCollection.Name)]
public sealed class LoginEndpointTests : IDisposable
{
    private const string Endpoint = "/api/auth/login";

    private readonly ApiPostgreSqlFixture _fixture;
    private readonly SmartPropertyApiFactory _factory;
    private readonly AuthScenario _scenario;

    public LoginEndpointTests(ApiPostgreSqlFixture fixture)
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
    public async Task PendingUser_Returns403AccountUnavailable()
    {
        var workspaceId = await _scenario.CreateWorkspaceAsync();

        using var client = _factory.CreateClient();

        // Registered and never activated, which is the state registration leaves every new account in.
        var user = await _scenario.RegisterAsync(client, workspaceId);

        using var response = await client.PostAsJsonAsync(
            Endpoint,
            new LoginRequest(user.Email, AuthScenario.Password));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);

        var error = (await response.Content.ReadFromJsonAsync<ApiErrorResponse>())!;
        Assert.Equal("authentication.account_unavailable", error.Code);
        Assert.Equal(StatusCodes.Status403Forbidden, error.Status);

        // A rejected login issues nothing.
        await using var context = _fixture.CreateContext();
        Assert.False(await context.RefreshTokens.AnyAsync(candidate => candidate.UserId == user.UserId));
    }

    [Fact]
    public async Task ActiveUser_Returns200WithATokenPair()
    {
        using var client = _factory.CreateClient();
        var user = await _scenario.RegisterActiveUserAsync(client);

        using var response = await client.PostAsJsonAsync(
            Endpoint,
            new LoginRequest(user.Email, AuthScenario.Password));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = (await response.Content.ReadFromJsonAsync<LoginResponse>())!;

        // Presence and ordering only. No token value is asserted, compared, or reported.
        Assert.False(string.IsNullOrWhiteSpace(body.AccessToken));
        Assert.False(string.IsNullOrWhiteSpace(body.RefreshToken));
        Assert.True(body.AccessTokenExpiresAt > DateTimeOffset.UtcNow);
        Assert.True(body.RefreshTokenExpiresAt > body.AccessTokenExpiresAt);
    }

    [Fact]
    public async Task ActiveUser_PersistsOnlyTheRefreshTokenHash()
    {
        using var client = _factory.CreateClient();
        var user = await _scenario.RegisterActiveUserAsync(client);

        var tokens = await AuthScenario.LoginAsync(client, user.Email);

        await using var context = _fixture.CreateContext();

        var stored = await context.RefreshTokens.SingleAsync(candidate => candidate.UserId == user.UserId);

        Assert.Equal(AuthScenario.HashRefreshToken(tokens.RefreshToken), stored.TokenHash);
        Assert.NotEqual(tokens.RefreshToken, stored.TokenHash);
        Assert.Null(stored.RevokedAt);
        Assert.True(stored.IsActive(DateTimeOffset.UtcNow));

        // The raw credential must appear nowhere in the table, under any row.
        Assert.False(await context.RefreshTokens
            .AnyAsync(candidate => candidate.TokenHash == tokens.RefreshToken));
    }

    [Fact]
    public async Task WrongPassword_Returns401InvalidCredentials()
    {
        using var client = _factory.CreateClient();
        var user = await _scenario.RegisterActiveUserAsync(client);

        using var response = await client.PostAsJsonAsync(
            Endpoint,
            new LoginRequest(user.Email, AuthScenario.Password + "-wrong"));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);

        var error = (await response.Content.ReadFromJsonAsync<ApiErrorResponse>())!;
        Assert.Equal("authentication.invalid_credentials", error.Code);
        Assert.Equal(StatusCodes.Status401Unauthorized, error.Status);

        await using var context = _fixture.CreateContext();
        Assert.False(await context.RefreshTokens.AnyAsync(candidate => candidate.UserId == user.UserId));
    }

    [Fact]
    public async Task UnknownEmail_ReturnsTheSameFailureAsAWrongPassword()
    {
        using var client = _factory.CreateClient();

        using var response = await client.PostAsJsonAsync(
            Endpoint,
            new LoginRequest(AuthScenario.UniqueEmail("login-unknown"), AuthScenario.Password));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);

        var error = (await response.Content.ReadFromJsonAsync<ApiErrorResponse>())!;

        // Byte-for-byte the wrong-password response above, so the pair cannot be told apart by their bodies.
        // Response time is deliberately not asserted: the dummy verification that equalises it is covered by
        // unit tests, and a wall-clock assertion here would be flaky.
        Assert.Equal("authentication.invalid_credentials", error.Code);
        Assert.Equal(StatusCodes.Status401Unauthorized, error.Status);
        Assert.Equal("Invalid email or password.", error.Message);
    }
}
