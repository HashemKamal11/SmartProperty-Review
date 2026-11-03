using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using SmartProperty.Api.Contracts.Workspaces;
using SmartProperty.Api.IntegrationTests.Infrastructure;
using SmartProperty.Application.Authorization;
using Xunit;

namespace SmartProperty.Api.IntegrationTests.Workspaces;

[Collection(ApiPostgreSqlCollection.Name)]
public sealed class WorkspaceMemberCreateEndpointTests(ApiPostgreSqlFixture fixture)
{
    private const string MembersRoute = "/api/workspaces/members";

    /// <summary>A workspace created through the platform endpoint together with its admin account.</summary>
    private async Task<(SmartPropertyApiFactory Factory, Guid WorkspaceId, Guid AdminUserId)> CreateWorkspaceWithAdminAsync()
    {
        var factory = new SmartPropertyApiFactory(fixture, usePersistencePermissionChecker: true);
        using var setup = factory.CreateClient();
        var scenario = new AuthScenario(fixture);
        var platform = await scenario.RegisterActiveUserAsync(setup);
        await scenario.GrantPlatformPermissionAsync(platform.UserId, PermissionCodes.WorkspaceAdminCreate);
        using var platformClient = factory.CreateAuthenticatedClient(platform.UserId);

        using var response = await platformClient.PostAsJsonAsync("/api/admin/workspaces", new
        {
            name = "Members " + Guid.NewGuid().ToString("N"),
            admin = new
            {
                email = AuthScenario.UniqueEmail("ws-admin"),
                password = AuthScenario.Password,
                firstName = "Sara",
                lastName = "Admin",
            },
        });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var created = (await response.Content.ReadFromJsonAsync<CreateWorkspaceResponse>())!;

        return (factory, created.Id, created.AdminUserId!.Value);
    }

    private static object Body(string email) =>
        new { email, password = AuthScenario.Password, firstName = "Mona", lastName = "Member" };

    [Fact]
    public async Task AWorkspaceAdminCreatesAMemberWhoCanSignInAndReadOnlyThatWorkspace()
    {
        var (factory, workspaceId, adminId) = await CreateWorkspaceWithAdminAsync();
        using var _ = factory;
        using var admin = factory.CreateAuthenticatedClient(adminId);
        var email = AuthScenario.UniqueEmail("member");

        using var response = await admin.PostAsJsonAsync(MembersRoute, Body(email));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var member = (await response.Content.ReadFromJsonAsync<WorkspaceMemberResponse>())!;
        Assert.Equal(workspaceId, member.WorkspaceId);
        Assert.Equal(email.ToLowerInvariant(), member.Email);
        Assert.Equal("Member", member.Role);
        Assert.DoesNotContain("password", await response.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);

        // The account signs in at once: nobody has to activate it.
        using var anonymous = factory.CreateClient();
        var tokens = await AuthScenario.LoginAsync(anonymous, email);
        using var memberClient = AuthScenario.Authenticate(factory.CreateClient(), tokens.AccessToken);
        Assert.Equal(HttpStatusCode.OK, (await memberClient.GetAsync($"/api/workspaces/{workspaceId}")).StatusCode);

        // A member holds workspace.read only: it cannot create members itself.
        using var attempt = await memberClient.PostAsJsonAsync(
            MembersRoute, Body(AuthScenario.UniqueEmail("nope")));
        Assert.Equal(HttpStatusCode.Forbidden, attempt.StatusCode);
    }

    [Fact]
    public async Task TheMemberRoleIsCreatedOnceAndSharedByLaterMembers()
    {
        var (factory, workspaceId, adminId) = await CreateWorkspaceWithAdminAsync();
        using var _ = factory;
        using var admin = factory.CreateAuthenticatedClient(adminId);

        for (var i = 0; i < 2; i++)
        {
            using var response = await admin.PostAsJsonAsync(
                MembersRoute, Body(AuthScenario.UniqueEmail("shared")));
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        }

        await using var db = fixture.CreateContext();
        var memberRoles = await db.Roles
            .Where(role => role.WorkspaceId == workspaceId && role.Name == "Member")
            .Select(role => role.Id)
            .ToListAsync();
        var roleId = Assert.Single(memberRoles);
        Assert.Equal(2, await db.WorkspaceMembershipRoles.CountAsync(link => link.RoleId == roleId));
        Assert.Equal(1, await db.RolePermissions.CountAsync(link => link.RoleId == roleId));
    }

    [Fact]
    public async Task ADuplicateEmailIsRefusedWith409AndSavesNothingNew()
    {
        var (factory, workspaceId, adminId) = await CreateWorkspaceWithAdminAsync();
        using var _ = factory;
        using var admin = factory.CreateAuthenticatedClient(adminId);
        var email = AuthScenario.UniqueEmail("dup");

        using var first = await admin.PostAsJsonAsync(MembersRoute, Body(email));
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        using var second = await admin.PostAsJsonAsync(MembersRoute, Body(email.ToUpperInvariant()));

        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        await using var db = fixture.CreateContext();
        Assert.Equal(1, await db.Users.CountAsync(user => user.Email == email.ToLowerInvariant()));
    }

    [Fact]
    public async Task AnInvalidBodyIs422()
    {
        var (factory, workspaceId, adminId) = await CreateWorkspaceWithAdminAsync();
        using var _ = factory;
        using var admin = factory.CreateAuthenticatedClient(adminId);

        using var response = await admin.PostAsJsonAsync(
            MembersRoute, new { email = "not-an-email", password = "x" });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }

    [Fact]
    public async Task EachAdminAddsMembersOnlyToTheirOwnWorkspaceWhateverTheBodySays()
    {
        var (factory, workspaceA, adminA) = await CreateWorkspaceWithAdminAsync();
        using var _a = factory;
        var (otherFactory, workspaceB, _) = await CreateWorkspaceWithAdminAsync();
        using var _b = otherFactory;
        using var admin = factory.CreateAuthenticatedClient(adminA);
        var email = AuthScenario.UniqueEmail("own-only");

        // A workspace id in the body is not a field: it cannot redirect the member to another workspace.
        using var response = await admin.PostAsJsonAsync(MembersRoute, new
        {
            email, password = AuthScenario.Password, firstName = "Mona", lastName = "Member", workspaceId = workspaceB,
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var member = (await response.Content.ReadFromJsonAsync<WorkspaceMemberResponse>())!;
        Assert.Equal(workspaceA, member.WorkspaceId);
        await using var db = fixture.CreateContext();
        Assert.False(await db.WorkspaceMemberships.AnyAsync(m => m.UserId == member.UserId && m.WorkspaceId == workspaceB));
    }

    [Fact]
    public async Task ASignedInUserWhoAdministersNoWorkspaceAndAnAnonymousCallerAreRefused()
    {
        var (factory, _, _) = await CreateWorkspaceWithAdminAsync();
        using var _f = factory;
        var outsider = await new AuthScenario(fixture).RegisterActiveUserAsync(factory.CreateClient());
        using var outsiderClient = factory.CreateAuthenticatedClient(outsider.UserId);
        var email = AuthScenario.UniqueEmail("blocked");

        using var forbidden = await outsiderClient.PostAsJsonAsync(MembersRoute, Body(email));
        using var anonymous = factory.CreateClient();
        using var unauthenticated = await anonymous.PostAsJsonAsync(MembersRoute, Body(email));

        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, unauthenticated.StatusCode);
        await using var db = fixture.CreateContext();
        Assert.False(await db.Users.AnyAsync(user => user.Email == email.ToLowerInvariant()));
    }
}
