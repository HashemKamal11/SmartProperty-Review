using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SmartProperty.Api.Contracts.Workspaces;
using SmartProperty.Api.IntegrationTests.Infrastructure;
using SmartProperty.Application.Authorization;
using Xunit;

namespace SmartProperty.Api.IntegrationTests.Workspaces;

[Collection(ApiPostgreSqlCollection.Name)]
public sealed class AdminWorkspaceCreateEndpointTests(ApiPostgreSqlFixture fixture)
{
    [Fact]
    public async Task CreateOnlyGrantPersistsWorkspaceWithoutAccessSideEffectsAndAllowsConcurrentDuplicateNames()
    {
        using var factory = new SmartPropertyApiFactory(fixture, usePersistencePermissionChecker: true);
        using var setup = factory.CreateClient();
        var scenario = new AuthScenario(fixture);
        var user = await scenario.RegisterActiveUserAsync(setup);
        await scenario.GrantPlatformPermissionAsync(user.UserId, PermissionCodes.WorkspaceAdminCreate);
        using var client = factory.CreateAuthenticatedClient(user.UserId);
        var before = await Counts();
        var name = "متجر  " + Guid.NewGuid().ToString("N");
        var responses = await Task.WhenAll(
            client.PostAsJsonAsync("/api/admin/workspaces", new { name = "  " + name + "  " }),
            client.PostAsJsonAsync("/api/admin/workspaces", new { name }));
        var ids = new List<Guid>();
        foreach (var response in responses)
        {
            using (response)
            {
                Assert.Equal(HttpStatusCode.Created, response.StatusCode);
                var item = (await response.Content.ReadFromJsonAsync<AdminWorkspaceResponse>())!;
                Assert.Equal(name, item.Name);
                Assert.Equal(item.CreatedAt, item.UpdatedAt);
                Assert.EndsWith($"/api/admin/workspaces/{item.Id}", response.Headers.Location!.ToString(), StringComparison.Ordinal);
                ids.Add(item.Id);
                Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync(response.Headers.Location)).StatusCode);
                Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync($"/api/workspaces/{item.Id}")).StatusCode);
            }
        }
        Assert.Equal(2, ids.Distinct().Count());
        var after = await Counts();
        Assert.Equal(before[0] + 2, after[0]);
        Assert.Equal(before.Skip(1), after.Skip(1));
        await using var db = fixture.CreateContext();
        Assert.Equal(2, await db.Workspaces.CountAsync(w => ids.Contains(w.Id) && w.Name == name));
        using var options = JsonDocument.Parse(await setup.GetStringAsync("/api/workspaces/registration-options"));
        var optionIds = options.RootElement.EnumerateArray().Select(x => x.GetProperty("id").GetGuid()).ToArray();
        Assert.All(ids, id => Assert.Contains(id, optionIds));
        using var context = JsonDocument.Parse(await client.GetStringAsync("/api/auth/context"));
        Assert.Empty(context.RootElement.GetProperty("workspaces").EnumerateArray());
    }

    [Theory]
    [InlineData("{}", 422)]
    [InlineData("{\"name\":null}", 422)]
    [InlineData("{\"name\":\"  \"}", 422)]
    [InlineData("{\"name\":\"A\\nB\"}", 422)]
    [InlineData("{\"name\":123}", 400)]
    [InlineData("{", 400)]
    public async Task InvalidRequestsDoNotPersist(string json, int status)
    {
        using var factory = new SmartPropertyApiFactory(fixture, usePersistencePermissionChecker: true);
        using var setup = factory.CreateClient();
        var scenario = new AuthScenario(fixture);
        var user = await scenario.RegisterActiveUserAsync(setup);
        await scenario.GrantPlatformPermissionAsync(user.UserId, PermissionCodes.WorkspaceAdminCreate);
        using var client = factory.CreateAuthenticatedClient(user.UserId);
        var before = await Counts();
        using var response = await client.PostAsync("/api/admin/workspaces", new StringContent(json, Encoding.UTF8, "application/json"));
        Assert.Equal(status, (int)response.StatusCode);
        using var error = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(status == 422 ? "workspace_admin.validation_failed" : "request.malformed", error.RootElement.GetProperty("code").GetString());
        Assert.Equal(before, await Counts());
    }

    private async Task<int[]> Counts()
    {
        await using var db = fixture.CreateContext();
        return [await db.Workspaces.CountAsync(), await db.WorkspaceMemberships.CountAsync(),
            await db.WorkspaceMembershipRoles.CountAsync(), await db.Roles.CountAsync(),
            await db.RolePermissions.CountAsync(), await db.Permissions.CountAsync(),
            await db.WorkspaceAccessRequests.CountAsync(), await db.UserPlatformRoles.CountAsync(),
            await db.Users.CountAsync()];
    }
}