using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using SmartProperty.Api.IntegrationTests.Infrastructure;
using SmartProperty.Application.Authorization;
using SmartProperty.Domain.Identity;
using Xunit;

namespace SmartProperty.Api.IntegrationTests.Workspaces;

[Collection(ApiPostgreSqlCollection.Name)]
public sealed class AdminWorkspaceReadEndpointTests(ApiPostgreSqlFixture fixture)
{
    [Fact]
    public async Task PlatformReadAllowsDirectoryAndDetailWithoutMembership()
    {
        using var factory = new SmartPropertyApiFactory(fixture, usePersistencePermissionChecker: true);
        using var setup = factory.CreateClient();
        var scenario = new AuthScenario(fixture);
        var user = await scenario.RegisterActiveUserAsync(setup);
        var workspace = await scenario.CreateWorkspaceAsync();
        await scenario.GrantPlatformPermissionAsync(user.UserId, PermissionCodes.WorkspaceAdminRead);
        using var client = factory.CreateAuthenticatedClient(user.UserId);
        using var page = await client.GetAsync("/api/admin/workspaces?page=1&pageSize=1");
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        using var json = JsonDocument.Parse(await page.Content.ReadAsStringAsync());
        Assert.Equal(1, json.RootElement.GetProperty("pageSize").GetInt32());
        Assert.Single(json.RootElement.GetProperty("items").EnumerateArray());
        Assert.True(json.RootElement.GetProperty("totalCount").GetInt64() > 0);
        using var detail = await client.GetAsync($"/api/admin/workspaces/{workspace}");
        Assert.Equal(HttpStatusCode.OK, detail.StatusCode);
        using var detailJson = JsonDocument.Parse(await detail.Content.ReadAsStringAsync());
        Assert.Equal(new[] { "createdAt", "id", "name", "updatedAt" },
            detailJson.RootElement.EnumerateObject().Select(x => x.Name).Order(StringComparer.Ordinal));
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync($"/api/workspaces/{workspace}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync("/api/admin/workspaces", new { name = "Denied" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/admin/workspaces/{Guid.Empty}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/admin/workspaces/{Guid.NewGuid()}")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/api/admin/workspaces?page=bad")).StatusCode);
        using var context = JsonDocument.Parse(await client.GetStringAsync("/api/auth/context"));
        Assert.Empty(context.RootElement.GetProperty("workspaces").EnumerateArray());
    }

    [Theory]
    [InlineData("anonymous")]
    [InlineData("role-name")]
    [InlineData("local")]
    [InlineData("inactive")]
    public async Task UnauthorizedPathsCannotReadOrCreate(string kind)
    {
        using var factory = new SmartPropertyApiFactory(fixture, usePersistencePermissionChecker: true);
        using var setup = factory.CreateClient();
        var scenario = new AuthScenario(fixture);
        var workspace = await scenario.CreateWorkspaceAsync();
        var user = await scenario.RegisterActiveUserAsync(setup);
        if (kind == "role-name")
        {
            await using var db = fixture.CreateContext();
            var role = new Role(Guid.NewGuid(), "Platform Admin", RoleScope.Platform, null, DateTimeOffset.UtcNow);
            // Reuse the role if another fixture scenario already created it, without adding grants.
            var existing = Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.FirstOrDefaultAsync(
                db.Roles, x => x.Scope == RoleScope.Platform && x.Name == "Platform Admin");
            var found = await existing;
            if (found is null) db.Roles.Add(role);
            db.UserPlatformRoles.Add(new UserPlatformRole(user.UserId, found?.Id ?? role.Id));
            await db.SaveChangesAsync();
        }
        if (kind == "local")
        {
            await scenario.GrantWorkspacePermissionAsync(user.UserId, workspace, PermissionCodes.WorkspaceAdminRead);
            // Use another workspace to avoid the helper creating a duplicate membership.
            await scenario.GrantWorkspacePermissionAsync(user.UserId, await scenario.CreateWorkspaceAsync(), PermissionCodes.WorkspaceAdminCreate);
        }
        if (kind == "inactive")
        {
            await scenario.GrantPlatformPermissionAsync(user.UserId, PermissionCodes.WorkspaceAdminRead);
            await scenario.GrantPlatformPermissionAsync(user.UserId, PermissionCodes.WorkspaceAdminCreate);
            await scenario.UpdateUserAsync(user.UserId, (u, time) => u.Suspend(time));
        }
        using var client = kind == "anonymous" ? factory.CreateClient() : factory.CreateAuthenticatedClient(user.UserId);
        var expected = kind == "anonymous" ? HttpStatusCode.Unauthorized : HttpStatusCode.Forbidden;
        Assert.Equal(expected, (await client.GetAsync("/api/admin/workspaces")).StatusCode);
        Assert.Equal(expected, (await client.GetAsync($"/api/admin/workspaces/{workspace}")).StatusCode);
        Assert.Equal(expected, (await client.PostAsJsonAsync("/api/admin/workspaces", new { name = "Denied" })).StatusCode);
    }
}