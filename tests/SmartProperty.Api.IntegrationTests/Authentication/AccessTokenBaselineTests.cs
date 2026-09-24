using System.Net;
using System.Net.Http.Headers;
using Microsoft.IdentityModel.JsonWebTokens;
using SmartProperty.Api.IntegrationTests.Infrastructure;
using SmartProperty.Application.Authorization;
using Xunit;

namespace SmartProperty.Api.IntegrationTests.Authentication;

/// <summary>
/// A small baseline for the access tokens the API issues and accepts: a valid one authenticates, an expired one
/// does not, and the token stays identity-only.
/// </summary>
/// <remarks>
/// This is deliberately not a full adversarial JWT suite. It fixes the properties the authorization model
/// depends on — chiefly that no role, permission, or workspace claim travels in the token, so authorization can
/// never be decided from a token alone.
/// </remarks>
[Collection(ApiPostgreSqlCollection.Name)]
public sealed class AccessTokenBaselineTests : IDisposable
{
    private const string Endpoint = "/__tests__/authorization/platform";

    private readonly SmartPropertyApiFactory _factory;

    public AccessTokenBaselineTests(ApiPostgreSqlFixture fixture)
    {
        _factory = new SmartPropertyApiFactory(fixture);
    }

    public void Dispose()
    {
        _factory.Dispose();
    }

    [Fact]
    public async Task ValidToken_Authenticates()
    {
        _factory.PermissionChecker.Decision = AuthorizationDecision.Allowed;
        using var client = _factory.CreateAuthenticatedClient(Guid.NewGuid());

        var response = await client.GetAsync(Endpoint);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task ValidToken_IdentifiesTheSubjectItWasIssuedFor()
    {
        var userId = Guid.NewGuid();
        _factory.PermissionChecker.Decision = AuthorizationDecision.Allowed;
        using var client = _factory.CreateAuthenticatedClient(userId);

        await client.GetAsync(Endpoint);

        Assert.Equal(userId, _factory.PermissionChecker.LastRequest!.UserId);
    }

    [Fact]
    public async Task ExpiredToken_Returns401AndNeverReachesThePermissionChecker()
    {
        // Issued two hours in the past with a fifteen-minute lifetime, so the outcome is far outside the
        // host's thirty-second clock skew and does not depend on how long the test takes.
        _factory.PermissionChecker.Decision = AuthorizationDecision.Allowed;
        using var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            TestJwt.CreateExpiredAccessToken(Guid.NewGuid()));

        var response = await client.GetAsync(Endpoint);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(0, _factory.PermissionChecker.CallCount);
    }

    [Fact]
    public async Task TokenSignedWithAnotherKey_Returns401()
    {
        _factory.PermissionChecker.Decision = AuthorizationDecision.Allowed;
        using var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            ForeignTokenIssuer.CreateAccessToken(Guid.NewGuid()));

        var response = await client.GetAsync(Endpoint);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(0, _factory.PermissionChecker.CallCount);
    }

    [Fact]
    public void IssuedToken_CarriesNoRolePermissionOrWorkspaceClaims()
    {
        var token = new JsonWebToken(TestJwt.CreateAccessToken(Guid.NewGuid()));

        var claimTypes = token.Claims
            .Select(claim => claim.Type)
            .ToArray();

        string[] forbiddenClaimFragments =
            ["role", "permission", "workspace", "membership", "scope", "admin"];

        foreach (var fragment in forbiddenClaimFragments)
        {
            Assert.DoesNotContain(
                claimTypes,
                claimType => claimType.Contains(fragment, StringComparison.OrdinalIgnoreCase));
        }
    }

    [Fact]
    public void IssuedToken_CarriesOnlyIdentityAndLifetimeClaims()
    {
        var token = new JsonWebToken(TestJwt.CreateAccessToken(Guid.NewGuid()));

        var claimTypes = token.Claims
            .Select(claim => claim.Type)
            .OrderBy(claimType => claimType, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(
            [
                JwtRegisteredClaimNames.Aud,
                JwtRegisteredClaimNames.Exp,
                JwtRegisteredClaimNames.Iat,
                JwtRegisteredClaimNames.Iss,
                JwtRegisteredClaimNames.Jti,
                JwtRegisteredClaimNames.Nbf,
                JwtRegisteredClaimNames.Sub
            ],
            claimTypes);
    }

    [Fact]
    public void IssuedToken_NamesTheConfiguredIssuerAndAudienceAndSubject()
    {
        var userId = Guid.NewGuid();

        var token = new JsonWebToken(TestJwt.CreateAccessToken(userId));

        Assert.Equal(TestJwt.Issuer, token.Issuer);
        Assert.Equal(TestJwt.Audience, Assert.Single(token.Audiences));
        Assert.Equal(userId.ToString(), token.Subject);
    }
}
