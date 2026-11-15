using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SmartProperty.Api.Contracts.Workspaces;
using SmartProperty.Api.IntegrationTests.Infrastructure;
using SmartProperty.Application.Authorization;
using SmartProperty.Application.Workspaces;
using Xunit;

namespace SmartProperty.Api.IntegrationTests.Workspaces;

/// <summary>
/// Company workspace registration end to end: sign-up (or an existing account applying), the platform administrator's
/// queue, approval creating the workspace with the applicant as its admin, and rejection with a reason.
/// </summary>
[Collection(ApiPostgreSqlCollection.Name)]
public sealed class WorkspaceRegistrationEndpointTests(ApiPostgreSqlFixture fixture)
{
    private const string AdminRoute = "/api/admin/workspace-registrations";

    [Fact]
    public async Task ACompanySignsUpAndApprovalMakesTheApplicantTheAdminOfTheNewWorkspace()
    {
        using var factory = new SmartPropertyApiFactory(fixture, usePersistencePermissionChecker: true);
        using var anonymous = factory.CreateClient();
        var scenario = new AuthScenario(fixture);
        var email = AuthScenario.UniqueEmail("company");
        var companyName = "Acme Realty " + Guid.NewGuid().ToString("N")[..8];

        using var signUp = await anonymous.PostAsJsonAsync("/api/auth/register-workspace", new
        {
            email,
            password = AuthScenario.Password,
            firstName = "Sara",
            lastName = "Owner",
            phoneNumber = AuthScenario.UniquePhone(),
            companyName = "  " + companyName + "  ",
            commercialRegistrationNumber = "CR-1010"
        });

        Assert.Equal(HttpStatusCode.Created, signUp.StatusCode);
        var registration = (await signUp.Content.ReadFromJsonAsync<RegisterWorkspaceResponse>())!;
        Assert.Equal("Pending", registration.Status);
        Assert.Equal("Pending", registration.RegistrationStatus);

        using var reviewer = await ReviewerAsync(factory, scenario, anonymous);

        // The request is in the queue, but cannot be approved before the applicant verifies the phone and email.
        using (var queue = JsonDocument.Parse(await reviewer.GetStringAsync(AdminRoute + "?pageSize=100")))
        {
            Assert.Contains(
                queue.RootElement.GetProperty("items").EnumerateArray(),
                item => item.GetProperty("id").GetGuid() == registration.RegistrationRequestId
                    && item.GetProperty("companyName").GetString() == companyName
                    && !item.GetProperty("applicant").GetProperty("contactVerified").GetBoolean());
        }

        using (var early = await reviewer.PostAsync($"{AdminRoute}/{registration.RegistrationRequestId}/approve", null))
        {
            Assert.Equal(HttpStatusCode.Conflict, early.StatusCode);
            Assert.Contains("workspace_registrations.contact_not_verified", await early.Content.ReadAsStringAsync());
        }

        await scenario.UpdateUserAsync(registration.UserId, (user, now) =>
        {
            user.VerifyPhone(now);
            user.VerifyEmail(now);
            user.ActivateWhenContactVerified(now);
        });

        using var applicant = factory.CreateAuthenticatedClient(registration.UserId);
        var mine = (await applicant.GetFromJsonAsync<WorkspaceRegistrationResponse[]>("/api/workspace-registrations/mine"))!;
        Assert.Equal("Pending", Assert.Single(mine).Status);

        using var approve = await reviewer.PostAsync($"{AdminRoute}/{registration.RegistrationRequestId}/approve", null);
        Assert.Equal(HttpStatusCode.OK, approve.StatusCode);
        var approval = (await approve.Content.ReadFromJsonAsync<ApproveWorkspaceRegistrationResponse>())!;
        Assert.Equal("Approved", approval.Status);
        Assert.Equal(companyName, approval.WorkspaceName);
        Assert.Equal(registration.UserId, approval.ApplicantUserId);

        // The applicant now administers the workspace: reads it and creates its members.
        Assert.Equal(HttpStatusCode.OK, (await applicant.GetAsync($"/api/workspaces/{approval.WorkspaceId}")).StatusCode);
        using (var member = await applicant.PostAsJsonAsync("/api/workspaces/members", new
               {
                   email = AuthScenario.UniqueEmail("member"),
                   password = AuthScenario.Password,
                   firstName = "Mona",
                   lastName = "Member"
               }))
        {
            Assert.Equal(HttpStatusCode.Created, member.StatusCode);
        }

        await using (var db = fixture.CreateContext())
        {
            var role = await db.Roles.SingleAsync(r => r.WorkspaceId == approval.WorkspaceId && r.Name == WorkspaceRoles.AdminRoleName);
            Assert.True(await db.WorkspaceMembershipRoles.AnyAsync(a => a.RoleId == role.Id && a.WorkspaceMembershipId == approval.MembershipId));
        }

        mine = (await applicant.GetFromJsonAsync<WorkspaceRegistrationResponse[]>("/api/workspace-registrations/mine"))!;
        Assert.Equal(approval.WorkspaceId, Assert.Single(mine).WorkspaceId);

        using var again = await reviewer.PostAsync($"{AdminRoute}/{registration.RegistrationRequestId}/approve", null);
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
        Assert.Contains("workspace_registrations.already_reviewed", await again.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task AnExistingAccountAppliesOnceIsRejectedWithAReasonAndMayApplyAgain()
    {
        using var factory = new SmartPropertyApiFactory(fixture, usePersistencePermissionChecker: true);
        using var setup = factory.CreateClient();
        var scenario = new AuthScenario(fixture);
        var user = await scenario.RegisterActiveUserAsync(setup);
        using var applicant = factory.CreateAuthenticatedClient(user.UserId);

        using var apply = await applicant.PostAsJsonAsync("/api/workspace-registrations", new { companyName = "Nile Homes" });
        Assert.Equal(HttpStatusCode.Created, apply.StatusCode);
        var request = (await apply.Content.ReadFromJsonAsync<WorkspaceRegistrationResponse>())!;
        Assert.Null(request.CommercialRegistrationNumber);

        using (var duplicate = await applicant.PostAsJsonAsync("/api/workspace-registrations", new { companyName = "Nile Homes 2" }))
        {
            Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
        }

        using var reviewer = await ReviewerAsync(factory, scenario, setup);

        using (var noReason = await reviewer.PostAsJsonAsync($"{AdminRoute}/{request.Id}/reject", new { reason = " " }))
        {
            Assert.Equal(HttpStatusCode.UnprocessableEntity, noReason.StatusCode);
        }

        using var reject = await reviewer.PostAsJsonAsync($"{AdminRoute}/{request.Id}/reject", new { reason = "Commercial registration missing" });
        Assert.Equal(HttpStatusCode.OK, reject.StatusCode);

        var detail = (await reviewer.GetFromJsonAsync<WorkspaceRegistrationResponse>($"{AdminRoute}/{request.Id}"))!;
        Assert.Equal("Rejected", detail.Status);
        Assert.Equal("Commercial registration missing", detail.RejectionReason);
        Assert.Null(detail.WorkspaceId);

        using var retry = await applicant.PostAsJsonAsync("/api/workspace-registrations", new { companyName = "Nile Homes", commercialRegistrationNumber = "CR-77" });
        Assert.Equal(HttpStatusCode.Created, retry.StatusCode);
    }

    [Fact]
    public async Task TheAdminEndpointsRequireTheReviewPermission()
    {
        using var factory = new SmartPropertyApiFactory(fixture, usePersistencePermissionChecker: true);
        using var setup = factory.CreateClient();
        var user = await new AuthScenario(fixture).RegisterActiveUserAsync(setup);
        using var client = factory.CreateAuthenticatedClient(user.UserId);

        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync(AdminRoute)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsync($"{AdminRoute}/{Guid.NewGuid()}/approve", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await setup.GetAsync("/api/workspace-registrations/mine")).StatusCode);
    }

    private static async Task<HttpClient> ReviewerAsync(SmartPropertyApiFactory factory, AuthScenario scenario, HttpClient setup)
    {
        var reviewer = await scenario.RegisterActiveUserAsync(setup);
        await scenario.GrantPlatformPermissionAsync(reviewer.UserId, PermissionCodes.WorkspaceRegistrationsReview);
        return factory.CreateAuthenticatedClient(reviewer.UserId);
    }
}
