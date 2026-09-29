using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using SmartProperty.Api.Contracts.Authentication;
using SmartProperty.Api.IntegrationTests.Infrastructure;
using SmartProperty.Application.Authorization;
using SmartProperty.Application.Identity.Bootstrap;
using SmartProperty.Domain.Identity;
using Xunit;

namespace SmartProperty.Api.IntegrationTests.Deployment;

[Collection(ApiPostgreSqlCollection.Name)]
public sealed class FreshEnvironmentSmokeTests(ApiPostgreSqlFixture fixture)
{
    [Fact]
    public async Task TwoStageFirstBoot_ProducesAReadyApiAndAnActivePlatformAdministrator()
    {
        var connectionString = await fixture.CreateBlankDatabaseAsync();
        var workspaceId = Guid.NewGuid();
        var email = AuthScenario.UniqueEmail("first-boot-admin");

        await MigrationProvisioningTests.RunMigratorAsync(
            connectionString,
            enabled: true,
            workspaceId.ToString(),
            "First Boot Workspace");

        Guid userId;
        using (var firstStart = new SmartPropertyApiFactory(connectionString))
        using (var client = firstStart.CreateClient())
        {
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health/ready")).StatusCode);

            using var registration = await AuthScenario.PostRegisterAsync(client, email, workspaceId);
            Assert.Equal(HttpStatusCode.Created, registration.StatusCode);

            var registered = (await registration.Content.ReadFromJsonAsync<RegisterResponse>())!;
            Assert.Equal(nameof(UserStatus.Pending), registered.Status);
            Assert.Equal(workspaceId, registered.WorkspaceId);
            userId = registered.UserId;
        }

        using var secondStart = new SmartPropertyApiFactory(
            connectionString,
            usePersistencePermissionChecker: true,
            configuration:
            [
                new KeyValuePair<string, string?>("Bootstrap:Enabled", "true"),
                new KeyValuePair<string, string?>("Bootstrap:PlatformAdminEmail", email)
            ]);

        using (var startupClient = secondStart.CreateClient())
        {
            Assert.Equal(HttpStatusCode.OK, (await startupClient.GetAsync("/health/live")).StatusCode);
        }

        using (var loginClient = secondStart.CreateClient())
        {
            var tokens = await AuthScenario.LoginAsync(loginClient, email);

            using var adminClient = AuthScenario.Authenticate(secondStart.CreateClient(), tokens.AccessToken);
            using var adminResponse = await adminClient.GetAsync(
                "/api/admin/workspace-access-requests?pageNumber=1&pageSize=20");

            Assert.Equal(HttpStatusCode.OK, adminResponse.StatusCode);
        }

        await using var context = ApiPostgreSqlFixture.CreateContext(connectionString);

        var user = await context.Users.SingleAsync(candidate => candidate.Id == userId);
        Assert.Equal(UserStatus.Active, user.Status);

        var permission = await context.Permissions.SingleAsync(candidate =>
            candidate.Code == PermissionCodes.WorkspaceAccessRequestsReview);
        var role = await context.Roles.SingleAsync(candidate =>
            candidate.Scope == RoleScope.Platform
            && candidate.Name == BootstrapPlatformAdminCommandHandler.PlatformAdminRoleName);

        Assert.True(await context.RolePermissions.AnyAsync(candidate =>
            candidate.RoleId == role.Id && candidate.PermissionId == permission.Id));
        Assert.True(await context.UserPlatformRoles.AnyAsync(candidate =>
            candidate.UserId == userId && candidate.RoleId == role.Id));
    }
}
