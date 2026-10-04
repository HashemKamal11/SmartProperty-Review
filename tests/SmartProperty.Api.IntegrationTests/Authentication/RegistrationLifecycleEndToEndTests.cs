using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using SmartProperty.Api.Contracts;
using SmartProperty.Api.Contracts.Administration;
using SmartProperty.Api.Contracts.Authentication;
using SmartProperty.Api.Contracts.Workspaces;
using SmartProperty.Api.IntegrationTests.Infrastructure;
using SmartProperty.Application.Authorization;
using SmartProperty.Domain.Identity;
using SmartProperty.Domain.Workspaces;
using Xunit;

namespace SmartProperty.Api.IntegrationTests.Authentication;

/// <summary>
/// The whole frontend registration journey in one scenario, through the real HTTP endpoints only: discover a
/// workspace, register, be refused sign-in while Pending, be approved by a real administrator, then sign in, read
/// the profile, refresh, and log out.
/// </summary>
/// <remarks>
/// Nothing is substituted. The host keeps the production permission checker, and the reviewer is an Active user
/// holding the review permission through a platform role, signing in through the real login endpoint. The only
/// direct database writes are the ones no API exists for: creating the workspace and granting the reviewer role.
/// Each step's database effect is asserted on a separate context, so it is the stored state that is checked.
/// </remarks>
[Collection(ApiPostgreSqlCollection.Name)]
public sealed class RegistrationLifecycleEndToEndTests : IDisposable
{
    private readonly ApiPostgreSqlFixture _fixture;
    private readonly SmartPropertyApiFactory _factory;
    private readonly AuthScenario _scenario;

    public RegistrationLifecycleEndToEndTests(ApiPostgreSqlFixture fixture)
    {
        _fixture = fixture;
        _factory = new SmartPropertyApiFactory(fixture, usePersistencePermissionChecker: true);
        _scenario = new AuthScenario(fixture);
    }

    public void Dispose()
    {
        _factory.Dispose();
    }

    [Fact]
    public async Task RegisterApproveLoginMeRefreshLogout_RunsEndToEndAgainstTheRealBackend()
    {
        var workspaceId = await _scenario.CreateWorkspaceAsync();
        var email = AuthScenario.UniqueEmail("lifecycle");
        using var anonymous = _factory.CreateClient();

        // 1. The registration page discovers the workspace anonymously.
        var options = (await anonymous.GetFromJsonAsync<RegistrationWorkspaceOptionResponse[]>(
            "/api/workspaces/registration-options"))!;
        var chosen = Assert.Single(options, option => option.Id == workspaceId);

        // 2. Register with the chosen workspace.
        using var registerResponse = await anonymous.PostAsJsonAsync(
            "/api/auth/register",
            new RegisterRequest(email, AuthScenario.Password, "Lifecycle", "Applicant", chosen.Id));
        Assert.Equal(HttpStatusCode.Created, registerResponse.StatusCode);

        var registered = (await registerResponse.Content.ReadFromJsonAsync<RegisterResponse>())!;
        Assert.Equal(nameof(UserStatus.Pending), registered.Status);
        Assert.Equal(workspaceId, registered.WorkspaceId);
        Assert.Equal(nameof(WorkspaceAccessRequestStatus.Pending), registered.WorkspaceAccessStatus);

        // 3. Both the user and the request are stored as Pending.
        await using (var context = _fixture.CreateContext())
        {
            var user = await context.Users.SingleAsync(candidate => candidate.Id == registered.UserId);
            Assert.Equal(UserStatus.Pending, user.Status);

            var request = await context.WorkspaceAccessRequests
                .SingleAsync(candidate => candidate.Id == registered.WorkspaceAccessRequestId);
            Assert.Equal(WorkspaceAccessRequestStatus.Pending, request.Status);
            Assert.Equal(registered.UserId, request.UserId);
            Assert.Equal(workspaceId, request.WorkspaceId);
        }

        // 4. A Pending account cannot sign in, even with the right password.
        using (var pendingLogin = await anonymous.PostAsJsonAsync(
            "/api/auth/login",
            new LoginRequest(email, AuthScenario.Password)))
        {
            Assert.Equal(HttpStatusCode.Forbidden, pendingLogin.StatusCode);
            var error = (await pendingLogin.Content.ReadFromJsonAsync<ApiErrorResponse>())!;
            Assert.Equal("authentication.account_unavailable", error.Code);
        }

        // 5. A real administrator signs in and finds the request in the queue.
        using var admin = await SignInReviewerAsync();

        var queue = (await admin.GetFromJsonAsync<PagedResponse<WorkspaceAccessRequestResponse>>(
            $"/api/admin/workspace-access-requests?workspaceId={workspaceId}&status=Pending"))!;
        var queued = Assert.Single(queue.Items);
        Assert.Equal(registered.WorkspaceAccessRequestId, queued.RequestId);
        Assert.Equal(registered.UserId, queued.UserId);
        Assert.Equal(email, queued.UserEmail);
        Assert.Equal("Lifecycle", queued.UserFirstName);
        Assert.Equal("Applicant", queued.UserLastName);
        Assert.Equal(chosen.Name, queued.WorkspaceName);
        Assert.Equal(nameof(WorkspaceAccessRequestStatus.Pending), queued.Status);
        Assert.Null(queued.ReviewedAt);
        Assert.Null(queued.ReviewedByUserId);

        // 6. Approve.
        using var approveResponse = await admin.PostAsync(
            $"/api/admin/workspace-access-requests/{queued.RequestId}/approve",
            content: null);
        Assert.Equal(HttpStatusCode.OK, approveResponse.StatusCode);

        var approval = (await approveResponse.Content.ReadFromJsonAsync<ApproveWorkspaceAccessRequestResponse>())!;
        Assert.Equal(nameof(WorkspaceAccessRequestStatus.Approved), approval.RequestStatus);
        Assert.Equal(nameof(UserStatus.Active), approval.UserStatus);

        // 7. Stored state: Approved, Active, one membership, and no workspace role.
        await using (var context = _fixture.CreateContext())
        {
            var request = await context.WorkspaceAccessRequests
                .SingleAsync(candidate => candidate.Id == registered.WorkspaceAccessRequestId);
            Assert.Equal(WorkspaceAccessRequestStatus.Approved, request.Status);
            Assert.NotNull(request.ReviewedAt);
            Assert.NotNull(request.ReviewedByUserId);

            var user = await context.Users.SingleAsync(candidate => candidate.Id == registered.UserId);
            Assert.Equal(UserStatus.Active, user.Status);

            var membership = await context.WorkspaceMemberships.SingleAsync(candidate =>
                candidate.UserId == registered.UserId && candidate.WorkspaceId == workspaceId);
            Assert.Equal(approval.MembershipId, membership.Id);

            Assert.False(await context.WorkspaceMembershipRoles
                .AnyAsync(candidate => candidate.WorkspaceMembershipId == membership.Id));
        }

        // 8. The same credentials now sign in.
        using var loginResponse = await anonymous.PostAsJsonAsync(
            "/api/auth/login",
            new LoginRequest(email, AuthScenario.Password));
        Assert.Equal(HttpStatusCode.OK, loginResponse.StatusCode);
        var login = (await loginResponse.Content.ReadFromJsonAsync<LoginResponse>())!;
        Assert.False(string.IsNullOrWhiteSpace(login.AccessToken));
        Assert.False(string.IsNullOrWhiteSpace(login.RefreshToken));

        // 9. /me answers with the registered profile.
        using (var authenticated = AuthScenario.Authenticate(_factory.CreateClient(), login.AccessToken))
        {
            using var meResponse = await authenticated.GetAsync("/api/auth/me");
            Assert.Equal(HttpStatusCode.OK, meResponse.StatusCode);

            var me = (await meResponse.Content.ReadFromJsonAsync<MeResponse>())!;
            Assert.Equal(new MeResponse(registered.UserId, email, "Lifecycle", "Applicant"), me);
        }

        // 10. Refresh rotates the token: a new pair is issued and the presented token is spent.
        using var refreshResponse = await AuthScenario.PostRefreshAsync(anonymous, login.RefreshToken);
        Assert.Equal(HttpStatusCode.OK, refreshResponse.StatusCode);
        var refreshed = (await refreshResponse.Content.ReadFromJsonAsync<RefreshResponse>())!;
        Assert.NotEqual(login.RefreshToken, refreshed.RefreshToken);

        using (var replay = await AuthScenario.PostRefreshAsync(anonymous, login.RefreshToken))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, replay.StatusCode);
        }

        // 11. Logout revokes the current refresh token, which then cannot be used.
        using (var logout = await AuthScenario.PostLogoutAsync(anonymous, refreshed.RefreshToken))
        {
            Assert.Equal(HttpStatusCode.NoContent, logout.StatusCode);
        }

        using (var afterLogout = await AuthScenario.PostRefreshAsync(anonymous, refreshed.RefreshToken))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, afterLogout.StatusCode);
            var error = (await afterLogout.Content.ReadFromJsonAsync<ApiErrorResponse>())!;
            Assert.Equal("authentication.invalid_refresh_token", error.Code);
        }
    }

    /// <summary>
    /// An Active user holding the review permission through a platform role, signed in through the real login
    /// endpoint, so the approval is authorized by the production permission checker against stored rows.
    /// </summary>
    private async Task<HttpClient> SignInReviewerAsync()
    {
        using var setupClient = _factory.CreateClient();
        var reviewer = await _scenario.RegisterActiveUserAsync(setupClient);
        await _scenario.GrantPlatformPermissionAsync(reviewer.UserId, PermissionCodes.WorkspaceAccessRequestsReview);

        var tokens = await AuthScenario.LoginAsync(setupClient, reviewer.Email);

        return AuthScenario.Authenticate(_factory.CreateClient(), tokens.AccessToken);
    }
}
