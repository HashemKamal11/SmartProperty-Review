using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using SmartProperty.Api.Contracts.Administration;
using SmartProperty.Api.IntegrationTests.Infrastructure;
using SmartProperty.Application.Authorization;
using SmartProperty.Application.Identity.Bootstrap;
using SmartProperty.Domain.Identity;
using Xunit;

namespace SmartProperty.Api.IntegrationTests.Administration;

/// <summary>
/// The platform administrator bootstrap against the real host and a real migrated database: that it stays inert by
/// default, what one enabled run provisions, that a second run provisions nothing, and that the account it produces
/// can really review a request.
/// </summary>
/// <remarks>
/// Every configuration value here is supplied by the test through the factory. No environment variable, developer
/// setting, staging account, or developer database is read or written — the database is the collection fixture's
/// throwaway container.
///
/// The provisioned administrator's permission is then exercised through the production permission checker, so the
/// chain from "an operator ran bootstrap" to "this account may approve" is verified end to end rather than assumed.
/// </remarks>
[Collection(ApiPostgreSqlCollection.Name)]
public sealed class PlatformAdminBootstrapTests(ApiPostgreSqlFixture fixture)
{
    private const string BaseRoute = "/api/admin/workspace-access-requests";

    private readonly ApiPostgreSqlFixture _fixture = fixture;
    private readonly AuthScenario _scenario = new(fixture);

    [Fact]
    public async Task ByDefaultBootstrapIsDisabledAndProvisionsNothing()
    {
        var candidate = await RegisterCandidateAsync();

        // No Bootstrap configuration at all: exactly what a deployment that never sets it looks like.
        using var factory = new SmartPropertyApiFactory(_fixture);
        using var client = factory.CreateClient();

        // Forces the host to start, so a bootstrap would have run by now if one were going to.
        using var probe = await client.GetAsync("/health/live");
        probe.EnsureSuccessStatusCode();

        await using var context = _fixture.CreateContext();

        var user = await context.Users.SingleAsync(row => row.Id == candidate.UserId);
        Assert.Equal(UserStatus.Pending, user.Status);
        Assert.False(await context.UserPlatformRoles.AnyAsync(row => row.UserId == candidate.UserId));
    }

    [Fact]
    public async Task AnEnabledRunProvisionsThePermissionTheRoleTheAttachmentAndTheAssignment()
    {
        var candidate = await RegisterCandidateAsync();

        using (var factory = BootstrapFactory(candidate.Email))
        {
            using var client = factory.CreateClient();
            using var probe = await client.GetAsync("/health/live");
            probe.EnsureSuccessStatusCode();
        }

        await using var context = _fixture.CreateContext();

        var user = await context.Users.SingleAsync(row => row.Id == candidate.UserId);
        Assert.Equal(UserStatus.Active, user.Status);

        var permission = await context.Permissions.SingleAsync(
            row => row.Code == PermissionCodes.WorkspaceAccessRequestsReview);

        var role = await context.Roles.SingleAsync(row =>
            row.Name == BootstrapPlatformAdminCommandHandler.PlatformAdminRoleName
            && row.Scope == RoleScope.Platform);
        Assert.Null(role.WorkspaceId);

        Assert.True(await context.RolePermissions.AnyAsync(
            row => row.RoleId == role.Id && row.PermissionId == permission.Id));
        Assert.True(await context.UserPlatformRoles.AnyAsync(
            row => row.RoleId == role.Id && row.UserId == candidate.UserId));
    }

    [Fact]
    public async Task ASecondRunDuplicatesNothing()
    {
        var candidate = await RegisterCandidateAsync();

        for (var run = 0; run < 2; run++)
        {
            using var factory = BootstrapFactory(candidate.Email);
            using var client = factory.CreateClient();
            using var probe = await client.GetAsync("/health/live");
            probe.EnsureSuccessStatusCode();
        }

        await using var context = _fixture.CreateContext();

        Assert.Equal(
            1,
            await context.Permissions.CountAsync(
                row => row.Code == PermissionCodes.WorkspaceAccessRequestsReview));
        Assert.Equal(
            1,
            await context.Roles.CountAsync(row =>
                row.Name == BootstrapPlatformAdminCommandHandler.PlatformAdminRoleName
                && row.Scope == RoleScope.Platform));
        Assert.Equal(
            1,
            await context.UserPlatformRoles.CountAsync(row => row.UserId == candidate.UserId));
    }

    [Fact]
    public async Task TheBootstrappedAdministratorCanReviewARequest()
    {
        var candidate = await RegisterCandidateAsync();

        // The permission checker is the production one, so this only passes if bootstrap wrote a real grant.
        using var factory = BootstrapFactory(candidate.Email, usePersistencePermissionChecker: true);

        var applicantWorkspaceId = await _scenario.CreateWorkspaceAsync();
        RegisteredUser applicant;
        using (var registrationClient = factory.CreateClient())
        {
            applicant = await _scenario.RegisterAsync(registrationClient, applicantWorkspaceId);
        }

        using var client = AuthScenario.Authenticate(
            factory.CreateClient(),
            TestJwt.CreateAccessToken(candidate.UserId));

        var response = await client.PostAsync($"{BaseRoute}/{applicant.WorkspaceAccessRequestId}/approve", null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = (await response.Content.ReadFromJsonAsync<ApproveWorkspaceAccessRequestResponse>())!;
        Assert.Equal(applicant.UserId, body.UserId);
        Assert.Equal(nameof(UserStatus.Active), body.UserStatus);
    }

    [Fact]
    public async Task AnUnknownConfiguredEmailStopsTheHostWithAnActionableFailure()
    {
        var unknownEmail = AuthScenario.UniqueEmail("never-registered");

        using var factory = BootstrapFactory(unknownEmail);

        // Bootstrap was deliberately enabled, so a misconfiguration must be loud rather than silently skipped.
        var failure = await Assert.ThrowsAnyAsync<Exception>(async () =>
        {
            using var client = factory.CreateClient();
            using var probe = await client.GetAsync("/health/live");
        });

        Assert.Contains("bootstrap", Describe(failure), StringComparison.OrdinalIgnoreCase);

        // And nothing was provisioned on the way out.
        await using var context = _fixture.CreateContext();
        Assert.False(await context.Users.AnyAsync(row => row.Email == unknownEmail));
    }

    [Fact]
    public async Task AnEnabledBootstrapWithNoEmailStopsTheHost()
    {
        using var factory = new SmartPropertyApiFactory(
            _fixture,
            configuration:
            [
                new KeyValuePair<string, string?>("Bootstrap:Enabled", "true"),
                new KeyValuePair<string, string?>("Bootstrap:PlatformAdminEmail", string.Empty)
            ]);

        var failure = await Assert.ThrowsAnyAsync<Exception>(async () =>
        {
            using var client = factory.CreateClient();
            using var probe = await client.GetAsync("/health/live");
        });

        Assert.Contains("Bootstrap", Describe(failure), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ADeactivatedCandidateIsRefusedRatherThanRevived()
    {
        var candidate = await RegisterCandidateAsync();
        await _scenario.UpdateUserAsync(candidate.UserId, (user, now) => user.Deactivate(now));

        using var factory = BootstrapFactory(candidate.Email);

        await Assert.ThrowsAnyAsync<Exception>(async () =>
        {
            using var client = factory.CreateClient();
            using var probe = await client.GetAsync("/health/live");
        });

        await using var context = _fixture.CreateContext();

        var user = await context.Users.SingleAsync(row => row.Id == candidate.UserId);
        Assert.Equal(UserStatus.Deactivated, user.Status);
        Assert.False(await context.UserPlatformRoles.AnyAsync(row => row.UserId == candidate.UserId));
    }

    /// <summary>A host with bootstrap enabled for one test-created account, and nothing else changed.</summary>
    private SmartPropertyApiFactory BootstrapFactory(
        string platformAdminEmail,
        bool usePersistencePermissionChecker = false)
    {
        return new SmartPropertyApiFactory(
            _fixture,
            usePersistencePermissionChecker,
            [
                new KeyValuePair<string, string?>("Bootstrap:Enabled", "true"),
                new KeyValuePair<string, string?>("Bootstrap:PlatformAdminEmail", platformAdminEmail)
            ]);
    }

    /// <summary>
    /// A registered Pending account to elevate, created through the real registration endpoint on a host with
    /// bootstrap disabled. Bootstrap must find an account, never create one.
    /// </summary>
    private async Task<RegisteredUser> RegisterCandidateAsync()
    {
        var workspaceId = await _scenario.CreateWorkspaceAsync();

        using var factory = new SmartPropertyApiFactory(_fixture);
        using var client = factory.CreateClient();

        return await _scenario.RegisterAsync(client, workspaceId);
    }

    /// <summary>Every message in the chain, since host startup failures arrive wrapped.</summary>
    private static string Describe(Exception exception)
    {
        var messages = new List<string>();

        for (var current = exception; current is not null; current = current.InnerException)
        {
            messages.Add(current.Message);
        }

        return string.Join(" | ", messages);
    }
}
