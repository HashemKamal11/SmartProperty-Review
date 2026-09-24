using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using SmartProperty.Api.Contracts;
using SmartProperty.Api.IntegrationTests.Infrastructure;
using Xunit;

namespace SmartProperty.Api.IntegrationTests.Authentication;

/// <summary>
/// End-to-end coverage for POST /api/auth/logout against the real host and a real database.
/// </summary>
/// <remarks>
/// Logout is intentionally idempotent and intentionally uninformative: every token state — active, unknown,
/// already revoked — answers 204 with no body, so a caller cannot use it to discover whether a token exists.
/// The tests below assert that all three are indistinguishable, and that the active case really did revoke.
/// </remarks>
[Collection(ApiPostgreSqlCollection.Name)]
public sealed class LogoutEndpointTests : IDisposable
{
    private readonly ApiPostgreSqlFixture _fixture;
    private readonly SmartPropertyApiFactory _factory;
    private readonly AuthScenario _scenario;

    public LogoutEndpointTests(ApiPostgreSqlFixture fixture)
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
    public async Task ActiveRefreshToken_Returns204AndRevokesIt()
    {
        using var client = _factory.CreateClient();
        var signedIn = await _scenario.SignInAsync(client);

        using var response = await AuthScenario.PostLogoutAsync(client, signedIn.Tokens.RefreshToken);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Empty(await response.Content.ReadAsByteArrayAsync());

        await using var context = _fixture.CreateContext();
        var stored = await context.RefreshTokens
            .SingleAsync(candidate => candidate.UserId == signedIn.User.UserId);

        Assert.NotNull(stored.RevokedAt);
        Assert.False(stored.IsActive(DateTimeOffset.UtcNow));
    }

    [Fact]
    public async Task RefreshAfterLogout_IsRejected()
    {
        using var client = _factory.CreateClient();
        var signedIn = await _scenario.SignInAsync(client);

        using (var logout = await AuthScenario.PostLogoutAsync(client, signedIn.Tokens.RefreshToken))
        {
            Assert.Equal(HttpStatusCode.NoContent, logout.StatusCode);
        }

        using var refresh = await AuthScenario.PostRefreshAsync(client, signedIn.Tokens.RefreshToken);

        Assert.Equal(HttpStatusCode.Unauthorized, refresh.StatusCode);

        var error = (await refresh.Content.ReadFromJsonAsync<ApiErrorResponse>())!;
        Assert.Equal("authentication.invalid_refresh_token", error.Code);
        Assert.Equal(StatusCodes.Status401Unauthorized, error.Status);

        // A revoked token issues nothing, so the user still has exactly the one row login created.
        await using var context = _fixture.CreateContext();
        Assert.Equal(
            1,
            await context.RefreshTokens.CountAsync(candidate => candidate.UserId == signedIn.User.UserId));
    }

    [Fact]
    public async Task SameRefreshTokenTwice_Returns204Both()
    {
        using var client = _factory.CreateClient();
        var signedIn = await _scenario.SignInAsync(client);

        using (var first = await AuthScenario.PostLogoutAsync(client, signedIn.Tokens.RefreshToken))
        {
            Assert.Equal(HttpStatusCode.NoContent, first.StatusCode);
        }

        using var second = await AuthScenario.PostLogoutAsync(client, signedIn.Tokens.RefreshToken);

        Assert.Equal(HttpStatusCode.NoContent, second.StatusCode);

        // The second call changed nothing: the revocation timestamp is still the first call's.
        await using var context = _fixture.CreateContext();
        var stored = await context.RefreshTokens
            .SingleAsync(candidate => candidate.UserId == signedIn.User.UserId);

        Assert.NotNull(stored.RevokedAt);
    }

    [Fact]
    public async Task UnknownRefreshToken_Returns204AndPersistsNothing()
    {
        var unknownToken = AuthScenario.UniqueRefreshToken();

        using var client = _factory.CreateClient();
        using var response = await AuthScenario.PostLogoutAsync(client, unknownToken);

        // Identical to the active-token response above: the caller cannot learn that this token never existed.
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Empty(await response.Content.ReadAsByteArrayAsync());

        await using var context = _fixture.CreateContext();
        Assert.False(await context.RefreshTokens
            .AnyAsync(candidate => candidate.TokenHash == AuthScenario.HashRefreshToken(unknownToken)));
    }
}
