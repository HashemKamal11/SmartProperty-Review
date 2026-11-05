using System.Net;
using System.Net.Http.Headers;
using System.Text;
using SmartProperty.Api.IntegrationTests.Infrastructure;
using SmartProperty.Application.Authorization;
using Xunit;

namespace SmartProperty.Api.IntegrationTests.Marketplace.Admin;

/// <summary>
/// The activated Platform Admin review reads on the <i>production</i> composition: who is let in, and that nothing
/// that decides or writes is reachable beside them.
/// </summary>
/// <remarks>
/// No database is involved. Every request here is answered before a handler or the repository runs: by the
/// authentication challenge, by the permission door, or by the router. The permission checker is the test double
/// the other API tests use, so the answers prove how the controller asks, not what a database holds; the
/// PostgreSQL-backed counterpart is <see cref="AdminReviewReadEndToEndTests"/>.
/// </remarks>
public sealed class AdminReviewReadActivationTests
{
    private const string Route = "/api/admin/marketplace/listing-submissions";
    private static readonly Guid Reviewer = Guid.Parse("11111111-1111-1111-1111-111111111111");

    private static AdminReviewApiTestHost ProductionHost() =>
        new(new StubReviewerListingSubmissionReadRepository(), mount: false);

    [Theory]
    [InlineData(Route)]
    [InlineData(Route + "/22222222-2222-2222-2222-222222222222")]
    public async Task AnUnauthenticatedCallerIs401AndNothingIsAsked(string route)
    {
        using var host = ProductionHost();
        using var client = host.CreateClient();

        using var response = await client.GetAsync(route);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Contains("Bearer", response.Headers.WwwAuthenticate.Select(header => header.Scheme));
        Assert.Equal(0, host.Permissions.CallCount);
    }

    [Theory]
    [InlineData(Route)]
    [InlineData(Route + "/22222222-2222-2222-2222-222222222222")]
    public async Task AnAuthenticatedCallerWithoutTheReadPermissionIs403(string route)
    {
        using var host = ProductionHost();
        using var client = host.CreateClientFor(Reviewer);

        using var response = await client.GetAsync(route);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.DoesNotContain("marketplace.", await response.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(Route)]
    [InlineData(Route + "/22222222-2222-2222-2222-222222222222")]
    public async Task HoldingOnlyTheDecidePermissionDoesNotAllowReading(string route)
    {
        using var host = ProductionHost();
        host.Permissions.AllowedPermissionCodes.Add(PermissionCodes.MarketplaceSubmissionsReviewDecide);
        using var client = host.CreateClientFor(Reviewer);

        using var response = await client.GetAsync(route);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Theory]
    [InlineData("approve")]
    [InlineData("reject")]
    public async Task ApproveAndRejectStayUnroutableEvenForACallerHoldingBothPermissions(string action)
    {
        using var host = ProductionHost();
        host.Permissions.AllowedPermissionCodes.Add(PermissionCodes.MarketplaceSubmissionsReviewRead);
        host.Permissions.AllowedPermissionCodes.Add(PermissionCodes.MarketplaceSubmissionsReviewDecide);
        using var client = host.CreateClientFor(Reviewer);
        var route = $"{Route}/{Guid.NewGuid()}/{action}";

        using var post = await client.PostAsync(route, new StringContent("""{"expectedVersion":1,"reason":"x"}""", Encoding.UTF8, new MediaTypeHeaderValue("application/json")));
        using var get = await client.GetAsync(route);

        Assert.Equal(HttpStatusCode.NotFound, post.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, get.StatusCode);
    }

    [Theory]
    [InlineData("POST")]
    [InlineData("PUT")]
    [InlineData("PATCH")]
    [InlineData("DELETE")]
    public async Task OnlyGetIsAcceptedOnTheTwoReviewRoutes(string method)
    {
        using var host = ProductionHost();
        host.Permissions.AllowedPermissionCodes.Add(PermissionCodes.MarketplaceSubmissionsReviewRead);
        host.Permissions.AllowedPermissionCodes.Add(PermissionCodes.MarketplaceSubmissionsReviewDecide);
        using var client = host.CreateClientFor(Reviewer);

        foreach (var route in new[] { Route, $"{Route}/{Guid.NewGuid()}" })
        {
            using var request = new HttpRequestMessage(new HttpMethod(method), route)
            {
                Content = new StringContent("{}", Encoding.UTF8, new MediaTypeHeaderValue("application/json")),
            };
            using var response = await client.SendAsync(request);

            Assert.Contains(response.StatusCode, new[] { HttpStatusCode.MethodNotAllowed, HttpStatusCode.NotFound });
        }
    }

    [Fact]
    public async Task ANonGuidIdMatchesNoRoute()
    {
        using var host = ProductionHost();
        host.Permissions.AllowedPermissionCodes.Add(PermissionCodes.MarketplaceSubmissionsReviewRead);
        using var client = host.CreateClientFor(Reviewer);

        using var response = await client.GetAsync($"{Route}/not-a-guid");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}
