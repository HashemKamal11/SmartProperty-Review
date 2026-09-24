using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using SmartProperty.Api.Contracts;
using SmartProperty.Api.Contracts.Authentication;
using SmartProperty.Api.IntegrationTests.Infrastructure;
using SmartProperty.Domain.Identity;
using Xunit;

namespace SmartProperty.Api.IntegrationTests.Authentication;

/// <summary>
/// End-to-end coverage for POST /api/auth/refresh against the real host and a real database.
/// </summary>
/// <remarks>
/// Rotation is the property that matters: a refresh token works exactly once, and every way of failing —
/// unknown, already rotated, revoked, expired — returns the same 401 so a caller cannot probe the token store.
/// </remarks>
[Collection(ApiPostgreSqlCollection.Name)]
public sealed class RefreshEndpointTests : IDisposable
{
    private const string InvalidRefreshToken = "authentication.invalid_refresh_token";

    private readonly ApiPostgreSqlFixture _fixture;
    private readonly SmartPropertyApiFactory _factory;
    private readonly AuthScenario _scenario;

    public RefreshEndpointTests(ApiPostgreSqlFixture fixture)
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
    public async Task ValidRefreshToken_Returns200WithANewTokenPair()
    {
        using var client = _factory.CreateClient();
        var signedIn = await _scenario.SignInAsync(client);

        using var response = await AuthScenario.PostRefreshAsync(client, signedIn.Tokens.RefreshToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = (await response.Content.ReadFromJsonAsync<RefreshResponse>())!;

        Assert.False(string.IsNullOrWhiteSpace(body.AccessToken));
        Assert.False(string.IsNullOrWhiteSpace(body.RefreshToken));
        Assert.NotEqual(signedIn.Tokens.RefreshToken, body.RefreshToken);
        Assert.True(body.AccessTokenExpiresAt > DateTimeOffset.UtcNow);
        Assert.True(body.RefreshTokenExpiresAt > body.AccessTokenExpiresAt);
    }

    [Fact]
    public async Task RotatedRefreshToken_RevokesTheOldRowAndStoresTheReplacement()
    {
        using var client = _factory.CreateClient();
        var signedIn = await _scenario.SignInAsync(client);

        using var response = await AuthScenario.PostRefreshAsync(client, signedIn.Tokens.RefreshToken);
        response.EnsureSuccessStatusCode();

        var body = (await response.Content.ReadFromJsonAsync<RefreshResponse>())!;

        await using var context = _fixture.CreateContext();

        var rows = await context.RefreshTokens
            .Where(candidate => candidate.UserId == signedIn.User.UserId)
            .ToListAsync();

        Assert.Equal(2, rows.Count);

        var rotated = rows.Single(row => row.TokenHash == AuthScenario.HashRefreshToken(signedIn.Tokens.RefreshToken));
        var replacement = rows.Single(row => row.TokenHash == AuthScenario.HashRefreshToken(body.RefreshToken));

        Assert.NotNull(rotated.RevokedAt);
        Assert.False(rotated.IsActive(DateTimeOffset.UtcNow));
        Assert.Null(replacement.RevokedAt);
        Assert.True(replacement.IsActive(DateTimeOffset.UtcNow));
    }

    [Fact]
    public async Task ReplayedRefreshToken_Returns401AndIssuesNothing()
    {
        using var client = _factory.CreateClient();
        var signedIn = await _scenario.SignInAsync(client);

        using (var first = await AuthScenario.PostRefreshAsync(client, signedIn.Tokens.RefreshToken))
        {
            first.EnsureSuccessStatusCode();
        }

        using var replay = await AuthScenario.PostRefreshAsync(client, signedIn.Tokens.RefreshToken);

        Assert.Equal(HttpStatusCode.Unauthorized, replay.StatusCode);

        var error = (await replay.Content.ReadFromJsonAsync<ApiErrorResponse>())!;
        Assert.Equal(InvalidRefreshToken, error.Code);
        Assert.Equal(StatusCodes.Status401Unauthorized, error.Status);

        // The replay must not have added a third row.
        await using var context = _fixture.CreateContext();
        Assert.Equal(
            2,
            await context.RefreshTokens.CountAsync(candidate => candidate.UserId == signedIn.User.UserId));
    }

    [Fact]
    public async Task UnknownRefreshToken_Returns401()
    {
        using var client = _factory.CreateClient();

        using var response = await AuthScenario.PostRefreshAsync(client, AuthScenario.UniqueRefreshToken());

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);

        var error = (await response.Content.ReadFromJsonAsync<ApiErrorResponse>())!;
        Assert.Equal(InvalidRefreshToken, error.Code);
        Assert.Equal(StatusCodes.Status401Unauthorized, error.Status);
    }

    [Fact]
    public async Task ExpiredRefreshToken_Returns401()
    {
        using var client = _factory.CreateClient();
        var user = await _scenario.RegisterActiveUserAsync(client);

        // Written with an expiry already in the past rather than by waiting: the row is created through the
        // domain model, and only the hash is stored, exactly as login would have stored it.
        var rawToken = AuthScenario.UniqueRefreshToken();
        var createdAt = DateTimeOffset.UtcNow.AddHours(-2);

        await using (var arrangement = _fixture.CreateContext())
        {
            arrangement.RefreshTokens.Add(new RefreshToken(
                Guid.NewGuid(),
                user.UserId,
                AuthScenario.HashRefreshToken(rawToken),
                createdAt,
                createdAt.AddHours(1)));

            await arrangement.SaveChangesAsync();
        }

        using var response = await AuthScenario.PostRefreshAsync(client, rawToken);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);

        var error = (await response.Content.ReadFromJsonAsync<ApiErrorResponse>())!;
        Assert.Equal(InvalidRefreshToken, error.Code);
        Assert.Equal(StatusCodes.Status401Unauthorized, error.Status);

        // An expired token is rejected before anything is mutated, so it is not revoked as a side effect.
        await using var context = _fixture.CreateContext();
        var stored = await context.RefreshTokens
            .SingleAsync(candidate => candidate.TokenHash == AuthScenario.HashRefreshToken(rawToken));

        Assert.Null(stored.RevokedAt);
    }

    [Fact]
    public async Task RefreshAfterTheAccountBecomesUnavailable_Returns403AndLeavesTheTokenUnrotated()
    {
        using var client = _factory.CreateClient();
        var signedIn = await _scenario.SignInAsync(client);

        await _scenario.UpdateUserAsync(signedIn.User.UserId, (user, now) => user.Suspend(now));

        using var response = await AuthScenario.PostRefreshAsync(client, signedIn.Tokens.RefreshToken);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);

        var error = (await response.Content.ReadFromJsonAsync<ApiErrorResponse>())!;
        Assert.Equal("authentication.account_unavailable", error.Code);
        Assert.Equal(StatusCodes.Status403Forbidden, error.Status);

        // The status check happens before any mutation, so the presented token is left exactly as it was.
        await using var context = _fixture.CreateContext();
        var stored = await context.RefreshTokens
            .SingleAsync(candidate => candidate.UserId == signedIn.User.UserId);

        Assert.Null(stored.RevokedAt);
    }
}
