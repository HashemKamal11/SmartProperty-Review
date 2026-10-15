using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SmartProperty.Api.IntegrationTests.Infrastructure;
using SmartProperty.Application.Authorization;
using SmartProperty.Domain.Workspaces;
using Xunit;

namespace SmartProperty.Api.IntegrationTests.Workspaces;

/// <summary>
/// <c>GET /api/workspaces/{workspaceId}</c> against the production permission checker and real rows.
/// </summary>
/// <remarks>
/// Every refusal is the same <c>403</c>: a stranger, a member without the permission, a platform administrator,
/// and a workspace that does not exist cannot be told apart. A member is made the way production makes one, by
/// approving a registration, so the workspace's own "Member" role is the real one.
/// </remarks>
[Collection(ApiPostgreSqlCollection.Name)]
public sealed class WorkspaceReadEndpointTests(ApiPostgreSqlFixture fixture)
{
    private const string CorrelationHeader = "X-Correlation-ID";

    private readonly AuthScenario _scenario = new(fixture);

    [Fact]
    public async Task AMemberHoldingTheMemberRoleGetsExactlyTheIdAndNameOfTheWorkspace()
    {
        using var factory = CreateFactory();
        var workspaceId = await _scenario.CreateWorkspaceAsync();
        var member = await CreateApprovedMemberAsync(factory, workspaceId);
        using var client = factory.CreateAuthenticatedClient(member.UserId);

        using var response = await client.GetAsync($"/api/workspaces/{workspaceId}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        await using var context = fixture.CreateContext();
        var workspace = await context.Workspaces.SingleAsync(candidate => candidate.Id == workspaceId);

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(
            ["id", "name"],
            document.RootElement.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal));
        Assert.Equal(workspace.Id, document.RootElement.GetProperty("id").GetGuid());
        Assert.Equal(workspace.Name, document.RootElement.GetProperty("name").GetString());
    }

    [Fact]
    public async Task AMemberOfAnotherWorkspaceOnlyIsForbidden()
    {
        using var factory = CreateFactory();
        var target = await _scenario.CreateWorkspaceAsync();
        var other = await _scenario.CreateWorkspaceAsync();
        var member = await CreateApprovedMemberAsync(factory, other);
        using var client = factory.CreateAuthenticatedClient(member.UserId);

        using var response = await client.GetAsync($"/api/workspaces/{target}");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task AnAuthenticatedUserWithNoMembershipIsForbidden()
    {
        using var factory = CreateFactory();
        using var setup = factory.CreateClient();
        var workspaceId = await _scenario.CreateWorkspaceAsync();
        var user = await _scenario.RegisterActiveUserAsync(setup);
        using var client = factory.CreateAuthenticatedClient(user.UserId);

        using var response = await client.GetAsync($"/api/workspaces/{workspaceId}");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task AWorkspaceThatDoesNotExistIsTheSame403AsANonMemberAndNeverA404()
    {
        using var factory = CreateFactory();
        using var setup = factory.CreateClient();
        var existing = await _scenario.CreateWorkspaceAsync();
        var user = await _scenario.RegisterActiveUserAsync(setup);
        using var client = factory.CreateAuthenticatedClient(user.UserId);

        using var nonMember = await GetWithCorrelationAsync(client, existing);
        using var missing = await GetWithCorrelationAsync(client, Guid.NewGuid());

        Assert.Equal(HttpStatusCode.Forbidden, nonMember.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, missing.StatusCode);
        Assert.Equal(
            await nonMember.Content.ReadAsStringAsync(),
            await missing.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task AMemberWhoseMembershipHasNoRoleIsForbidden()
    {
        using var factory = CreateFactory();
        using var setup = factory.CreateClient();
        var workspaceId = await _scenario.CreateWorkspaceAsync();
        var user = await _scenario.RegisterActiveUserAsync(setup);

        // A member as they existed before approval assigned a role: the membership row and nothing else.
        await using (var context = fixture.CreateContext())
        {
            context.WorkspaceMemberships.Add(
                new WorkspaceMembership(Guid.NewGuid(), user.UserId, workspaceId, DateTimeOffset.UtcNow));
            await context.SaveChangesAsync();
        }

        using var client = factory.CreateAuthenticatedClient(user.UserId);

        using var response = await client.GetAsync($"/api/workspaces/{workspaceId}");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task APlatformAdministratorWhoIsNotAMemberIsForbidden()
    {
        using var factory = CreateFactory();
        using var setup = factory.CreateClient();
        var workspaceId = await _scenario.CreateWorkspaceAsync();
        var admin = await _scenario.RegisterActiveUserAsync(setup);
        await _scenario.GrantPlatformPermissionAsync(admin.UserId, PermissionCodes.WorkspaceRead);
        await _scenario.GrantPlatformPermissionAsync(admin.UserId, PermissionCodes.WorkspaceAccessRequestsReview);
        using var client = factory.CreateAuthenticatedClient(admin.UserId);

        using var response = await client.GetAsync($"/api/workspaces/{workspaceId}");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task NoTokenReturns401()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();

        using var response = await client.GetAsync($"/api/workspaces/{Guid.NewGuid()}");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task AnIdThatIsNotAGuidReturns404()
    {
        using var factory = CreateFactory();
        using var setup = factory.CreateClient();
        var user = await _scenario.RegisterActiveUserAsync(setup);
        using var client = factory.CreateAuthenticatedClient(user.UserId);

        using var response = await client.GetAsync("/api/workspaces/abc");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task TheOpenApiDocumentListsTheRouteWithOnly200401403And404()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();
        using var document = JsonDocument.Parse(await client.GetStringAsync("/openapi/v1.json"));

        var path = document.RootElement
            .GetProperty("paths")
            .GetProperty("/api/workspaces/{workspaceId}");

        Assert.Equal(["get"], path.EnumerateObject().Select(property => property.Name));

        var responses = path.GetProperty("get").GetProperty("responses");

        Assert.Equal(
            ["200", "401", "403", "404"],
            responses.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal));
    }

    private SmartPropertyApiFactory CreateFactory()
    {
        return new SmartPropertyApiFactory(fixture, usePersistencePermissionChecker: true);
    }

    private static async Task<HttpResponseMessage> GetWithCorrelationAsync(HttpClient client, Guid workspaceId)
    {
        // The same correlation id on both requests, so the error bodies can be compared exactly.
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/api/workspaces/{workspaceId}");
        request.Headers.Add(CorrelationHeader, "workspace-read-403");

        return await client.SendAsync(request);
    }

    /// <summary>
    /// Registers an applicant and has a real reviewer approve it, which is what gives the applicant the
    /// workspace's "Member" role.
    /// </summary>
    private async Task<RegisteredUser> CreateApprovedMemberAsync(SmartPropertyApiFactory factory, Guid workspaceId)
    {
        using var setup = factory.CreateClient();
        var applicant = await _scenario.RegisterAsync(setup, workspaceId);
        var reviewer = await _scenario.RegisterActiveUserAsync(setup);
        await _scenario.GrantPlatformPermissionAsync(
            reviewer.UserId,
            PermissionCodes.WorkspaceAccessRequestsReview);

        using var reviewerClient = factory.CreateAuthenticatedClient(reviewer.UserId);
        using var approval = await reviewerClient.PostAsync(
            $"/api/admin/workspace-access-requests/{applicant.WorkspaceAccessRequestId}/approve",
            content: null);
        approval.EnsureSuccessStatusCode();

        return applicant;
    }
}
