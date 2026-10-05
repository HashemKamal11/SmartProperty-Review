using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SmartProperty.Api.Contracts.Authentication;
using SmartProperty.Api.IntegrationTests.Infrastructure;
using SmartProperty.Application.Authorization;
using SmartProperty.Application.Identity.Bootstrap;
using SmartProperty.Domain.Identity;
using SmartProperty.Domain.Workspaces;
using Xunit;

namespace SmartProperty.Api.IntegrationTests.Authentication;

[Collection(ApiPostgreSqlCollection.Name)]
public sealed class AuthContextEndpointTests
{
    private const string Route = "/api/auth/context";

    private readonly ApiPostgreSqlFixture _fixture;
    private readonly AuthScenario _scenario;

    public AuthContextEndpointTests(ApiPostgreSqlFixture fixture)
    {
        _fixture = fixture;
        _scenario = new AuthScenario(fixture);
    }

    [Fact]
    public async Task NoTokenReturns401()
    {
        using var factory = new SmartPropertyApiFactory(_fixture);
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(Route);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task ActiveUserWithNoAssignmentsGetsProfileEmptyCollectionsAndNoStore()
    {
        using var factory = new SmartPropertyApiFactory(_fixture);
        using var setup = factory.CreateClient();
        var user = await _scenario.RegisterActiveUserAsync(setup);
        using var client = factory.CreateAuthenticatedClient(user.UserId);

        using var response = await client.GetAsync(Route);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);

        var body = (await response.Content.ReadFromJsonAsync<AuthContextResponse>())!;
        Assert.Equal(user.UserId, body.User.Id);
        Assert.Equal(user.Email, body.User.Email);
        Assert.Equal("Test", body.User.FirstName);
        Assert.Equal("User", body.User.LastName);
        Assert.Empty(body.PlatformRoles);
        Assert.Empty(body.PlatformPermissions);
        Assert.Empty(body.Workspaces);
    }

    [Fact]
    public async Task BootstrappedPlatformAdminGetsOnlyPlatformRoleAndPermissions()
    {
        RegisteredUser candidate;
        using (var registrationFactory = new SmartPropertyApiFactory(_fixture))
        using (var registrationClient = registrationFactory.CreateClient())
        {
            var workspaceId = await _scenario.CreateWorkspaceAsync();
            candidate = await _scenario.RegisterAsync(registrationClient, workspaceId);
        }

        using var factory = new SmartPropertyApiFactory(
            _fixture,
            usePersistencePermissionChecker: true,
            configuration:
            [
                new KeyValuePair<string, string?>("Bootstrap:Enabled", "true"),
                new KeyValuePair<string, string?>("Bootstrap:PlatformAdminEmail", candidate.Email)
            ]);
        using var client = factory.CreateAuthenticatedClient(candidate.UserId);

        var body = (await client.GetFromJsonAsync<AuthContextResponse>(Route))!;

        Assert.Equal([BootstrapPlatformAdminCommandHandler.PlatformAdminRoleName], body.PlatformRoles);
        Assert.Equal(
            [PermissionCodes.PropertyCreate, PermissionCodes.WorkspaceAccessRequestsReview],
            body.PlatformPermissions);
        Assert.Empty(body.Workspaces);
    }

    [Fact]
    public async Task ApprovedOrdinaryUserGetsMembershipWithNoRolesOrPermissions()
    {
        using var factory = new SmartPropertyApiFactory(_fixture, usePersistencePermissionChecker: true);
        using var setup = factory.CreateClient();
        var workspaceId = await _scenario.CreateWorkspaceAsync();
        var applicant = await _scenario.RegisterAsync(setup, workspaceId);
        var reviewer = await _scenario.RegisterActiveUserAsync(setup);
        await _scenario.GrantPlatformPermissionAsync(
            reviewer.UserId,
            PermissionCodes.WorkspaceAccessRequestsReview);

        using (var reviewerClient = factory.CreateAuthenticatedClient(reviewer.UserId))
        using (var approval = await reviewerClient.PostAsync(
            $"/api/admin/workspace-access-requests/{applicant.WorkspaceAccessRequestId}/approve",
            content: null))
        {
            Assert.Equal(HttpStatusCode.OK, approval.StatusCode);
        }

        using var client = factory.CreateAuthenticatedClient(applicant.UserId);
        var body = (await client.GetFromJsonAsync<AuthContextResponse>(Route))!;

        var workspace = Assert.Single(body.Workspaces);
        Assert.Equal(workspaceId, workspace.Id);
        Assert.Empty(workspace.Roles);
        Assert.Empty(workspace.Permissions);
    }

    [Fact]
    public async Task MultipleWorkspacesAreReturnedByOrdinalNameThenId()
    {
        using var factory = new SmartPropertyApiFactory(_fixture);
        using var setup = factory.CreateClient();
        var user = await _scenario.RegisterActiveUserAsync(setup);

        var high = new Workspace(
            Guid.Parse("bbbbbbbb-0000-0000-0000-000000000000"),
            "Mid",
            DateTimeOffset.UtcNow);
        var low = new Workspace(
            Guid.Parse("aaaaaaaa-0000-0000-0000-000000000000"),
            "Mid",
            DateTimeOffset.UtcNow);
        var alpha = new Workspace(Guid.NewGuid(), "Alpha", DateTimeOffset.UtcNow);

        await using (var context = _fixture.CreateContext())
        {
            context.Workspaces.AddRange(high, low, alpha);
            context.WorkspaceMemberships.AddRange(
                new WorkspaceMembership(Guid.NewGuid(), user.UserId, high.Id, DateTimeOffset.UtcNow),
                new WorkspaceMembership(Guid.NewGuid(), user.UserId, low.Id, DateTimeOffset.UtcNow),
                new WorkspaceMembership(Guid.NewGuid(), user.UserId, alpha.Id, DateTimeOffset.UtcNow));
            await context.SaveChangesAsync();
        }

        using var client = factory.CreateAuthenticatedClient(user.UserId);
        var body = (await client.GetFromJsonAsync<AuthContextResponse>(Route))!;

        Assert.Equal(
            [alpha.Id, low.Id, high.Id],
            body.Workspaces.Select(workspace => workspace.Id));
    }

    [Theory]
    [InlineData(UserStatus.Pending)]
    [InlineData(UserStatus.Suspended)]
    [InlineData(UserStatus.Deactivated)]
    public async Task NonActiveUserReturns403(UserStatus status)
    {
        using var factory = new SmartPropertyApiFactory(_fixture);
        using var setup = factory.CreateClient();
        RegisteredUser user;

        if (status == UserStatus.Pending)
        {
            var workspaceId = await _scenario.CreateWorkspaceAsync();
            user = await _scenario.RegisterAsync(setup, workspaceId);
        }
        else
        {
            user = await _scenario.RegisterActiveUserAsync(setup);
            await _scenario.UpdateUserAsync(
                user.UserId,
                status == UserStatus.Suspended
                    ? (candidate, now) => candidate.Suspend(now)
                    : (candidate, now) => candidate.Deactivate(now));
        }

        using var client = factory.CreateAuthenticatedClient(user.UserId);
        using var response = await client.GetAsync(Route);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task ResponseContainsOnlyTheApprovedContractFields()
    {
        using var factory = new SmartPropertyApiFactory(_fixture);
        using var setup = factory.CreateClient();
        var user = await _scenario.RegisterActiveUserAsync(setup);
        using var client = factory.CreateAuthenticatedClient(user.UserId);

        using var document = JsonDocument.Parse(await client.GetStringAsync(Route));

        Assert.Equal(
            ["platformPermissions", "platformRoles", "user", "workspaces"],
            PropertyNames(document.RootElement));
        Assert.Equal(
            ["email", "firstName", "id", "lastName"],
            PropertyNames(document.RootElement.GetProperty("user")));

        var json = document.RootElement.GetRawText();
        foreach (var forbidden in new[]
                 {
                     "workspaceType", "currentWorkspace", "activityMode", "roleId", "permissionId",
                     "membershipId", "status", "accessToken", "refreshToken", "buy", "sell", "rent"
                 })
        {
            Assert.DoesNotContain(forbidden, json, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task MeResponseRemainsProfileOnly()
    {
        using var factory = new SmartPropertyApiFactory(_fixture);
        using var setup = factory.CreateClient();
        var user = await _scenario.RegisterActiveUserAsync(setup);
        using var client = factory.CreateAuthenticatedClient(user.UserId);

        using var document = JsonDocument.Parse(await client.GetStringAsync("/api/auth/me"));

        Assert.Equal(["email", "firstName", "lastName", "userId"], PropertyNames(document.RootElement));
    }

    private static string[] PropertyNames(JsonElement element)
    {
        return element.EnumerateObject()
            .Select(property => property.Name)
            .Order(StringComparer.Ordinal)
            .ToArray();
    }
}
