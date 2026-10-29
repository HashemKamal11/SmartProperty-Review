using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SmartProperty.Api.Contracts;
using SmartProperty.Api.Contracts.Administration;
using SmartProperty.Api.Contracts.Authentication;
using SmartProperty.Api.Infrastructure.Errors;
using SmartProperty.Api.IntegrationTests.Infrastructure;
using SmartProperty.Application.Abstractions.Persistence;
using SmartProperty.Application.Authorization;
using SmartProperty.Common.Pagination;
using SmartProperty.Domain.Identity;
using SmartProperty.Domain.Workspaces;
using Xunit;

namespace SmartProperty.Api.IntegrationTests.Administration;

/// <summary>
/// The workspace access request review endpoints end to end: the authorization boundary, what the database looks
/// like afterwards, and what the applicant can then do.
/// </summary>
/// <remarks>
/// The permission checker is substituted here, so these tests are about the endpoints and the workflow. Whether the
/// right person really holds the permission is asserted separately, against the production checker and real rows,
/// in <see cref="WorkspaceAccessRequestReviewPermissionTests"/>.
///
/// Registration and login always go through the real HTTP endpoints, so an approval is proved by the applicant
/// actually being able to sign in afterwards rather than by a status column alone.
/// </remarks>
[Collection(ApiPostgreSqlCollection.Name)]
public sealed class WorkspaceAccessRequestReviewEndpointTests : IDisposable
{
    private const string BaseRoute = "/api/admin/workspace-access-requests";

    private readonly ApiPostgreSqlFixture _fixture;
    private readonly SmartPropertyApiFactory _factory;
    private readonly AuthScenario _scenario;

    public WorkspaceAccessRequestReviewEndpointTests(ApiPostgreSqlFixture fixture)
    {
        _fixture = fixture;
        _factory = new SmartPropertyApiFactory(fixture);
        _scenario = new AuthScenario(fixture);
    }

    public void Dispose()
    {
        _factory.Dispose();
    }

    [Fact]
    public async Task ApproveWithoutAToken_Returns401AndNeverReachesThePermissionChecker()
    {
        using var client = _factory.CreateClient();

        var response = await client.PostAsync($"{BaseRoute}/{Guid.NewGuid()}/approve", content: null);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);

        var error = await ReadErrorAsync(response);
        Assert.Equal(ApiErrorCodes.Unauthorized, error.Code);
        Assert.Equal(StatusCodes.Status401Unauthorized, error.Status);
        Assert.Equal(
            JwtBearerDefaults.AuthenticationScheme,
            Assert.Single(response.Headers.WwwAuthenticate).Scheme);

        // The requirement is never even evaluated for an unauthenticated caller.
        Assert.Equal(0, _factory.PermissionChecker.CallCount);
    }

    [Fact]
    public async Task RejectWithoutAToken_Returns401()
    {
        using var client = _factory.CreateClient();

        var response = await client.PostAsync($"{BaseRoute}/{Guid.NewGuid()}/reject", content: null);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(ApiErrorCodes.Unauthorized, (await ReadErrorAsync(response)).Code);
    }

    [Fact]
    public async Task ListWithoutAToken_Returns401()
    {
        using var client = _factory.CreateClient();

        var response = await client.GetAsync(BaseRoute);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(ApiErrorCodes.Unauthorized, (await ReadErrorAsync(response)).Code);
    }

    [Fact]
    public async Task AnAuthenticatedCallerWithoutThePermission_Returns403()
    {
        _factory.PermissionChecker.Decision = AuthorizationDecision.Denied;
        var applicant = await RegisterApplicantAsync();
        var signedIn = await _scenario.SignInAsync(_factory.CreateClient());
        using var client = AuthScenario.Authenticate(_factory.CreateClient(), signedIn.Tokens.AccessToken);

        var response = await client.PostAsync($"{BaseRoute}/{applicant.WorkspaceAccessRequestId}/approve", null);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);

        var error = await ReadErrorAsync(response);
        Assert.Equal(ApiErrorCodes.Forbidden, error.Code);
        Assert.Equal(StatusCodes.Status403Forbidden, error.Status);

        // Being signed in and Active is not enough, and nothing was reviewed.
        await using var context = _fixture.CreateContext();
        var request = await context.WorkspaceAccessRequests
            .SingleAsync(candidate => candidate.Id == applicant.WorkspaceAccessRequestId);
        Assert.Equal(WorkspaceAccessRequestStatus.Pending, request.Status);
    }

    [Fact]
    public async Task ApproveAsksAboutTheReviewPermissionAgainstThePlatformTarget()
    {
        AllowReview();
        var applicant = await RegisterApplicantAsync();
        using var client = await CreateReviewerClientAsync();

        await client.PostAsync($"{BaseRoute}/{applicant.WorkspaceAccessRequestId}/approve", null);

        var request = Assert.Single(_factory.PermissionChecker.Requests);
        Assert.Equal(PermissionCodes.WorkspaceAccessRequestsReview, request.PermissionCode);
        Assert.Same(AuthorizationTarget.Platform, request.Target);
        Assert.Equal(RoleScope.Platform, request.Target.Scope);
        Assert.Null(request.Target.WorkspaceId);
    }

    [Fact]
    public async Task ApproveSucceedsAndReturnsTheResultingState()
    {
        AllowReview();
        var applicant = await RegisterApplicantAsync();
        using var client = await CreateReviewerClientAsync();

        var response = await client.PostAsync($"{BaseRoute}/{applicant.WorkspaceAccessRequestId}/approve", null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = (await response.Content.ReadFromJsonAsync<ApproveWorkspaceAccessRequestResponse>())!;
        Assert.Equal(applicant.WorkspaceAccessRequestId, body.RequestId);
        Assert.Equal(applicant.UserId, body.UserId);
        Assert.Equal(applicant.WorkspaceId, body.WorkspaceId);
        Assert.Equal(nameof(WorkspaceAccessRequestStatus.Approved), body.RequestStatus);
        Assert.Equal(nameof(UserStatus.Active), body.UserStatus);
        Assert.NotEqual(Guid.Empty, body.MembershipId);
    }

    [Fact]
    public async Task AfterApprovalTheDatabaseHoldsTheDecisionTheActivationAndTheMembershipAndTheMemberRole()
    {
        AllowReview();
        var applicant = await RegisterApplicantAsync();
        var reviewerId = await CreateReviewerAsync();
        using var client = AuthScenario.Authenticate(
            _factory.CreateClient(),
            TestJwt.CreateAccessToken(reviewerId));

        var response = await client.PostAsync($"{BaseRoute}/{applicant.WorkspaceAccessRequestId}/approve", null);
        response.EnsureSuccessStatusCode();
        var body = (await response.Content.ReadFromJsonAsync<ApproveWorkspaceAccessRequestResponse>())!;

        await using var context = _fixture.CreateContext();

        var request = await context.WorkspaceAccessRequests
            .SingleAsync(candidate => candidate.Id == applicant.WorkspaceAccessRequestId);
        Assert.Equal(WorkspaceAccessRequestStatus.Approved, request.Status);
        Assert.NotNull(request.ReviewedAt);

        // The reviewer is the authenticated subject, not anything the request body could have named.
        Assert.Equal(reviewerId, request.ReviewedByUserId);

        var user = await context.Users.SingleAsync(candidate => candidate.Id == applicant.UserId);
        Assert.Equal(UserStatus.Active, user.Status);

        var membership = await context.WorkspaceMemberships.SingleAsync(candidate =>
            candidate.UserId == applicant.UserId && candidate.WorkspaceId == applicant.WorkspaceId);
        Assert.Equal(body.MembershipId, membership.Id);

        // Approval grants membership plus exactly one role: the workspace's own "Member" role, which carries
        // workspace.read and nothing else. No platform role is granted.
        var membershipRole = await context.WorkspaceMembershipRoles
            .SingleAsync(candidate => candidate.WorkspaceMembershipId == membership.Id);

        var memberRole = await context.Roles.SingleAsync(candidate => candidate.Id == membershipRole.RoleId);
        Assert.Equal("Member", memberRole.Name);
        Assert.Equal(RoleScope.Workspace, memberRole.Scope);
        Assert.Equal(applicant.WorkspaceId, memberRole.WorkspaceId);

        var permissionCodes = await context.RolePermissions
            .Where(candidate => candidate.RoleId == memberRole.Id)
            .Join(
                context.Permissions,
                rolePermission => rolePermission.PermissionId,
                permission => permission.Id,
                (_, permission) => permission.Code)
            .ToListAsync();
        Assert.Equal([PermissionCodes.WorkspaceRead], permissionCodes);

        Assert.False(await context.UserPlatformRoles.AnyAsync(candidate => candidate.UserId == applicant.UserId));
    }

    [Fact]
    public async Task AnApprovedApplicantCanThenSignInThroughTheRealLoginEndpoint()
    {
        AllowReview();
        var applicant = await RegisterApplicantAsync();

        using (var reviewerClient = await CreateReviewerClientAsync())
        {
            var approval = await reviewerClient.PostAsync(
                $"{BaseRoute}/{applicant.WorkspaceAccessRequestId}/approve",
                null);
            approval.EnsureSuccessStatusCode();
        }

        using var client = _factory.CreateClient();
        using var login = await client.PostAsJsonAsync(
            "/api/auth/login",
            new LoginRequest(applicant.Email, AuthScenario.Password));

        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
    }

    [Fact]
    public async Task RejectSucceedsAndLeavesTheApplicantUnableToSignIn()
    {
        AllowReview();
        var applicant = await RegisterApplicantAsync();
        var reviewerId = await CreateReviewerAsync();

        using (var reviewerClient = AuthScenario.Authenticate(
            _factory.CreateClient(),
            TestJwt.CreateAccessToken(reviewerId)))
        {
            var response = await reviewerClient.PostAsync(
                $"{BaseRoute}/{applicant.WorkspaceAccessRequestId}/reject",
                null);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            var body = (await response.Content.ReadFromJsonAsync<RejectWorkspaceAccessRequestResponse>())!;
            Assert.Equal(nameof(WorkspaceAccessRequestStatus.Rejected), body.RequestStatus);
            Assert.Equal(nameof(UserStatus.Pending), body.UserStatus);
        }

        await using (var context = _fixture.CreateContext())
        {
            var request = await context.WorkspaceAccessRequests
                .SingleAsync(candidate => candidate.Id == applicant.WorkspaceAccessRequestId);
            Assert.Equal(WorkspaceAccessRequestStatus.Rejected, request.Status);
            Assert.Equal(reviewerId, request.ReviewedByUserId);
            Assert.NotNull(request.ReviewedAt);

            var user = await context.Users.SingleAsync(candidate => candidate.Id == applicant.UserId);
            Assert.Equal(UserStatus.Pending, user.Status);

            Assert.False(await context.WorkspaceMemberships
                .AnyAsync(candidate => candidate.UserId == applicant.UserId));
        }

        using var client = _factory.CreateClient();
        using var login = await client.PostAsJsonAsync(
            "/api/auth/login",
            new LoginRequest(applicant.Email, AuthScenario.Password));

        Assert.Equal(HttpStatusCode.Forbidden, login.StatusCode);
        Assert.Equal("authentication.account_unavailable", (await ReadErrorAsync(login)).Code);
    }

    [Fact]
    public async Task AnUnknownRequestId_Returns404()
    {
        AllowReview();
        using var client = await CreateReviewerClientAsync();

        var response = await client.PostAsync($"{BaseRoute}/{Guid.NewGuid()}/approve", null);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("workspace_access_requests.not_found", (await ReadErrorAsync(response)).Code);
    }

    [Theory]
    [InlineData("approve", "approve")]
    [InlineData("approve", "reject")]
    [InlineData("reject", "approve")]
    [InlineData("reject", "reject")]
    public async Task ASecondReview_Returns409AndChangesNothing(string first, string second)
    {
        AllowReview();
        var applicant = await RegisterApplicantAsync();
        using var client = await CreateReviewerClientAsync();

        using var firstResponse = await client.PostAsync(
            $"{BaseRoute}/{applicant.WorkspaceAccessRequestId}/{first}",
            null);
        firstResponse.EnsureSuccessStatusCode();

        var secondResponse = await client.PostAsync(
            $"{BaseRoute}/{applicant.WorkspaceAccessRequestId}/{second}",
            null);

        Assert.Equal(HttpStatusCode.Conflict, secondResponse.StatusCode);
        Assert.Equal(
            "workspace_access_requests.already_reviewed",
            (await ReadErrorAsync(secondResponse)).Code);

        await using var context = _fixture.CreateContext();

        var expectedStatus = first == "approve"
            ? WorkspaceAccessRequestStatus.Approved
            : WorkspaceAccessRequestStatus.Rejected;
        var request = await context.WorkspaceAccessRequests
            .SingleAsync(candidate => candidate.Id == applicant.WorkspaceAccessRequestId);

        // The first decision stands; the second never overwrites it.
        Assert.Equal(expectedStatus, request.Status);

        // And at most one membership exists, whatever the pair of attempts was.
        var membershipCount = await context.WorkspaceMemberships
            .CountAsync(candidate =>
                candidate.UserId == applicant.UserId && candidate.WorkspaceId == applicant.WorkspaceId);
        Assert.Equal(first == "approve" ? 1 : 0, membershipCount);
    }

    [Fact]
    public async Task AConcurrentReviewLoss_Returns409AlreadyReviewedAndPersistsNothing()
    {
        AllowReview();
        var applicant = await RegisterApplicantAsync();
        var reviewerId = await CreateReviewerAsync();

        using var conflictFactory = _factory.WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IUnitOfWork>();
                services.AddScoped<IUnitOfWork, ConcurrencyLosingUnitOfWork>();
            }));
        using var client = AuthScenario.Authenticate(
            conflictFactory.CreateClient(),
            TestJwt.CreateAccessToken(reviewerId));

        var response = await client.PostAsync(
            $"{BaseRoute}/{applicant.WorkspaceAccessRequestId}/approve",
            content: null);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var error = await ReadErrorAsync(response);
        Assert.Equal("workspace_access_requests.already_reviewed", error.Code);

        await using var context = _fixture.CreateContext();
        var request = await context.WorkspaceAccessRequests
            .SingleAsync(candidate => candidate.Id == applicant.WorkspaceAccessRequestId);
        var user = await context.Users.SingleAsync(candidate => candidate.Id == applicant.UserId);

        Assert.Equal(WorkspaceAccessRequestStatus.Pending, request.Status);
        Assert.Null(request.ReviewedByUserId);
        Assert.Null(request.ReviewedAt);
        Assert.Equal(UserStatus.Pending, user.Status);
        Assert.False(await context.WorkspaceMemberships.AnyAsync(candidate =>
            candidate.UserId == applicant.UserId && candidate.WorkspaceId == applicant.WorkspaceId));
    }

    [Fact]
    public async Task TheQueueListsThePendingRequestWithTheApplicantAndWorkspaceNames()
    {
        AllowReview();
        var applicant = await RegisterApplicantAsync();
        using var client = await CreateReviewerClientAsync();

        var response = await client.GetAsync($"{BaseRoute}?workspaceId={applicant.WorkspaceId}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = (await response.Content
            .ReadFromJsonAsync<PagedResponse<WorkspaceAccessRequestResponse>>())!;

        var item = Assert.Single(body.Items);
        Assert.Equal(applicant.WorkspaceAccessRequestId, item.RequestId);
        Assert.Equal(applicant.UserId, item.UserId);
        Assert.Equal(applicant.Email, item.UserEmail);
        Assert.Equal(applicant.WorkspaceId, item.WorkspaceId);
        Assert.False(string.IsNullOrWhiteSpace(item.WorkspaceName));
        Assert.Equal(nameof(WorkspaceAccessRequestStatus.Pending), item.Status);
        Assert.Null(item.ReviewedAt);
        Assert.Null(item.ReviewedByUserId);
        Assert.Equal(1, body.TotalCount);
    }

    [Fact]
    public async Task ExtremePage_ReturnsAnEmptyBoundedPageInsteadOfA500()
    {
        AllowReview();
        var applicant = await RegisterApplicantAsync();
        using var client = await CreateReviewerClientAsync();

        var response = await client.GetAsync(
            $"{BaseRoute}?workspaceId={applicant.WorkspaceId}&page={int.MaxValue}&pageSize=100");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = (await response.Content
            .ReadFromJsonAsync<PagedResponse<WorkspaceAccessRequestResponse>>())!;

        Assert.Empty(body.Items);
        Assert.Equal(PageParameters.MaxPageNumber, body.Page);
        Assert.Equal(PageParameters.MaxPageSize, body.PageSize);
        Assert.Equal(1, body.TotalCount);
        Assert.Equal(1, body.TotalPages);
        Assert.False(body.HasNext);
        Assert.True(body.HasPrevious);
    }

    [Fact]
    public async Task TheQueueDefaultsToPendingSoAReviewedRequestDropsOutOfIt()
    {
        AllowReview();
        var applicant = await RegisterApplicantAsync();
        using var client = await CreateReviewerClientAsync();

        using var approval = await client.PostAsync(
            $"{BaseRoute}/{applicant.WorkspaceAccessRequestId}/approve",
            null);
        approval.EnsureSuccessStatusCode();

        var pending = (await client
            .GetFromJsonAsync<PagedResponse<WorkspaceAccessRequestResponse>>(
                $"{BaseRoute}?workspaceId={applicant.WorkspaceId}"))!;
        var approved = (await client
            .GetFromJsonAsync<PagedResponse<WorkspaceAccessRequestResponse>>(
                $"{BaseRoute}?workspaceId={applicant.WorkspaceId}&status=Approved"))!;

        Assert.Empty(pending.Items);
        Assert.Equal(applicant.WorkspaceAccessRequestId, Assert.Single(approved.Items).RequestId);
    }

    [Fact]
    public async Task AnUnparseableStatus_Returns400MalformedRequest()
    {
        AllowReview();
        using var client = await CreateReviewerClientAsync();

        var response = await client.GetAsync($"{BaseRoute}?status=not-a-status");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(ApiErrorCodes.MalformedRequest, (await ReadErrorAsync(response)).Code);
    }

    [Fact]
    public async Task TheApplicantCannotApproveTheirOwnRequestWithoutThePermission()
    {
        _factory.PermissionChecker.Decision = AuthorizationDecision.Denied;
        var applicant = await RegisterApplicantAsync();

        // Given a usable token — the applicant is Pending, so activate first — the request is still refused.
        await _scenario.UpdateUserAsync(applicant.UserId, (user, now) => user.Activate(now));

        using var client = AuthScenario.Authenticate(
            _factory.CreateClient(),
            TestJwt.CreateAccessToken(applicant.UserId));

        var response = await client.PostAsync($"{BaseRoute}/{applicant.WorkspaceAccessRequestId}/approve", null);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(ApiErrorCodes.Forbidden, (await ReadErrorAsync(response)).Code);

        await using var context = _fixture.CreateContext();
        var request = await context.WorkspaceAccessRequests
            .SingleAsync(candidate => candidate.Id == applicant.WorkspaceAccessRequestId);
        Assert.Equal(WorkspaceAccessRequestStatus.Pending, request.Status);
    }

    // ---- P-008: the workspace's Member role must carry exactly workspace.read, or approval fails closed ----
    //
    // Approving into a happy workspace (Member created, correct Member reused) is already asserted above; these add
    // the refusals and the initialization case. Each Member role is seeded in the applicant's own workspace, which
    // is what keeps the shared database safe for the other tests in the collection.

    [Theory]
    [InlineData(PermissionCodes.PropertyRead, false)]
    [InlineData(PermissionCodes.PropertyRead, true)]
    [InlineData(PermissionCodes.WorkspaceAccessRequestsReview, false)]
    public async Task AMemberRoleCarryingAnythingBesidesWorkspaceRead_Returns409AndChangesNothing(
        string otherPermissionCode,
        bool alsoCarriesWorkspaceRead)
    {
        AllowReview();
        var applicant = await RegisterApplicantAsync();
        var codes = alsoCarriesWorkspaceRead
            ? new[] { PermissionCodes.WorkspaceRead, otherPermissionCode }
            : new[] { otherPermissionCode };
        var memberRoleId = await SeedMemberRoleAsync(applicant.WorkspaceId, codes);
        using var client = await CreateReviewerClientAsync();

        using var response = await client.PostAsync($"{BaseRoute}/{applicant.WorkspaceAccessRequestId}/approve", null);

        await AssertMemberRoleMisconfiguredAsync(response);
        await AssertUnreviewedAsync(applicant);

        // Reported, never repaired: the role carries exactly what it carried, and nobody holds it.
        Assert.Equal(codes.Order(StringComparer.Ordinal), await PermissionCodesOfAsync(memberRoleId));

        await using var context = _fixture.CreateContext();
        Assert.False(await context.WorkspaceMembershipRoles.AnyAsync(candidate => candidate.RoleId == memberRoleId));
    }

    [Fact]
    public async Task AnEmptyMemberRoleAlreadyAssignedToAnotherMembership_Returns409AndChangesNothing()
    {
        AllowReview();
        var applicant = await RegisterApplicantAsync();
        var memberRoleId = await SeedMemberRoleAsync(applicant.WorkspaceId);
        await SeedHolderAsync(applicant.WorkspaceId, memberRoleId);
        using var client = await CreateReviewerClientAsync();

        using var response = await client.PostAsync($"{BaseRoute}/{applicant.WorkspaceAccessRequestId}/approve", null);

        await AssertMemberRoleMisconfiguredAsync(response);
        await AssertUnreviewedAsync(applicant);

        // An empty role that someone already holds is not quietly completed: it stays empty and held once.
        Assert.Empty(await PermissionCodesOfAsync(memberRoleId));

        await using var context = _fixture.CreateContext();
        Assert.Equal(1, await context.WorkspaceMembershipRoles.CountAsync(candidate => candidate.RoleId == memberRoleId));
    }

    [Fact]
    public async Task ARequestRefusedWithMemberRoleMisconfigured_StaysPendingAndCanThenBeRejectedNormally()
    {
        AllowReview();
        var applicant = await RegisterApplicantAsync();
        await SeedMemberRoleAsync(applicant.WorkspaceId, PermissionCodes.PropertyRead);
        using var client = await CreateReviewerClientAsync();

        using (var approval = await client.PostAsync($"{BaseRoute}/{applicant.WorkspaceAccessRequestId}/approve", null))
        {
            await AssertMemberRoleMisconfiguredAsync(approval);
        }

        await AssertUnreviewedAsync(applicant);

        // The refusal decided nothing, so the ordinary reject path is unaffected by it.
        using var rejection = await client.PostAsync($"{BaseRoute}/{applicant.WorkspaceAccessRequestId}/reject", null);

        Assert.Equal(HttpStatusCode.OK, rejection.StatusCode);

        var body = (await rejection.Content.ReadFromJsonAsync<RejectWorkspaceAccessRequestResponse>())!;
        Assert.Equal(nameof(WorkspaceAccessRequestStatus.Rejected), body.RequestStatus);
        Assert.Equal(nameof(UserStatus.Pending), body.UserStatus);

        await using var context = _fixture.CreateContext();
        var request = await context.WorkspaceAccessRequests
            .SingleAsync(candidate => candidate.Id == applicant.WorkspaceAccessRequestId);
        Assert.Equal(WorkspaceAccessRequestStatus.Rejected, request.Status);
        Assert.NotNull(request.ReviewedByUserId);
        Assert.False(await context.WorkspaceMemberships.AnyAsync(candidate => candidate.UserId == applicant.UserId));
    }

    [Fact]
    public async Task AnEmptyUnassignedMemberRole_IsInitializedWithExactlyWorkspaceRead()
    {
        AllowReview();
        var applicant = await RegisterApplicantAsync();
        var memberRoleId = await SeedMemberRoleAsync(applicant.WorkspaceId);
        using var client = await CreateReviewerClientAsync();

        using var response = await client.PostAsync($"{BaseRoute}/{applicant.WorkspaceAccessRequestId}/approve", null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = (await response.Content.ReadFromJsonAsync<ApproveWorkspaceAccessRequestResponse>())!;

        // The existing role was completed and reused, not duplicated: exactly workspace.read, held once.
        Assert.Equal([PermissionCodes.WorkspaceRead], await PermissionCodesOfAsync(memberRoleId));

        await using var context = _fixture.CreateContext();
        var assignment = await context.WorkspaceMembershipRoles
            .SingleAsync(candidate => candidate.RoleId == memberRoleId);
        Assert.Equal(body.MembershipId, assignment.WorkspaceMembershipId);
        Assert.Equal(1, await context.Roles.CountAsync(candidate =>
            candidate.WorkspaceId == applicant.WorkspaceId && candidate.Name == "Member"));
    }

    [Fact]
    public async Task AnotherWorkspacesMisconfiguredMemberAndAPlatformRoleNamedMember_AreIsolatedFromAnApproval()
    {
        AllowReview();
        var applicant = await RegisterApplicantAsync();

        // Another workspace's Member role is misconfigured and held. It must neither block nor be touched.
        var otherWorkspaceId = await _scenario.CreateWorkspaceAsync();
        var otherRoleId = await SeedMemberRoleAsync(otherWorkspaceId, PermissionCodes.PropertyRead);
        await SeedHolderAsync(otherWorkspaceId, otherRoleId);

        // A PLATFORM role that happens to be named "Member", carrying a platform permission and held by a user.
        Guid platformHolderId;

        using (var setup = _factory.CreateClient())
        {
            platformHolderId = (await _scenario.RegisterActiveUserAsync(setup)).UserId;
        }

        var platformRole = new Role(Guid.NewGuid(), "Member", RoleScope.Platform, null, DateTimeOffset.UtcNow);

        await using (var seeding = _fixture.CreateContext())
        {
            var reviewPermission = await seeding.Permissions.FirstOrDefaultAsync(candidate =>
                candidate.Code == PermissionCodes.WorkspaceAccessRequestsReview);

            if (reviewPermission is null)
            {
                reviewPermission = new Permission(Guid.NewGuid(), PermissionCodes.WorkspaceAccessRequestsReview);
                seeding.Permissions.Add(reviewPermission);
            }

            seeding.Roles.Add(platformRole);
            seeding.RolePermissions.Add(new RolePermission(platformRole.Id, reviewPermission.Id));
            seeding.UserPlatformRoles.Add(new UserPlatformRole(platformHolderId, platformRole.Id));
            await seeding.SaveChangesAsync();
        }

        using var client = await CreateReviewerClientAsync();

        using var response = await client.PostAsync($"{BaseRoute}/{applicant.WorkspaceAccessRequestId}/approve", null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        await using var context = _fixture.CreateContext();

        // The applicant got a Member role of its own workspace carrying exactly workspace.read.
        var ownRole = await context.Roles.SingleAsync(candidate =>
            candidate.WorkspaceId == applicant.WorkspaceId && candidate.Name == "Member");
        Assert.NotEqual(otherRoleId, ownRole.Id);
        Assert.Equal(RoleScope.Workspace, ownRole.Scope);
        Assert.Equal([PermissionCodes.WorkspaceRead], await PermissionCodesOfAsync(ownRole.Id));

        // The other workspace's role: same permission, same single holder.
        Assert.Equal([PermissionCodes.PropertyRead], await PermissionCodesOfAsync(otherRoleId));
        Assert.Equal(1, await context.WorkspaceMembershipRoles.CountAsync(candidate => candidate.RoleId == otherRoleId));

        // The platform role: same platform permission, same single holder, and the applicant holds no platform role.
        Assert.Equal([PermissionCodes.WorkspaceAccessRequestsReview], await PermissionCodesOfAsync(platformRole.Id));
        Assert.Equal(
            platformHolderId,
            (await context.UserPlatformRoles.SingleAsync(candidate => candidate.RoleId == platformRole.Id)).UserId);
        Assert.False(await context.UserPlatformRoles.AnyAsync(candidate => candidate.UserId == applicant.UserId));
    }

    private static async Task AssertMemberRoleMisconfiguredAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);

        var raw = await response.Content.ReadAsStringAsync();
        var error = JsonSerializer.Deserialize<ApiErrorResponse>(raw, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;

        Assert.Equal("workspace_access_requests.member_role_misconfigured", error.Code);
        Assert.Equal(StatusCodes.Status409Conflict, error.Status);

        // Generic by design: the role's actual permission set is never exposed to the client.
        foreach (var permissionCode in new[]
                 {
                     PermissionCodes.WorkspaceRead,
                     PermissionCodes.PropertyRead,
                     PermissionCodes.WorkspaceAccessRequestsReview
                 })
        {
            Assert.DoesNotContain(permissionCode, raw, StringComparison.OrdinalIgnoreCase);
        }
    }

    // A request that no part of an approval touched: still Pending, no reviewer, a Pending applicant, no membership.
    private async Task AssertUnreviewedAsync(RegisteredUser applicant)
    {
        await using var context = _fixture.CreateContext();

        var request = await context.WorkspaceAccessRequests
            .SingleAsync(candidate => candidate.Id == applicant.WorkspaceAccessRequestId);
        Assert.Equal(WorkspaceAccessRequestStatus.Pending, request.Status);
        Assert.Null(request.ReviewedByUserId);
        Assert.Null(request.ReviewedAt);

        var user = await context.Users.SingleAsync(candidate => candidate.Id == applicant.UserId);
        Assert.Equal(UserStatus.Pending, user.Status);
        Assert.False(await context.WorkspaceMemberships.AnyAsync(candidate => candidate.UserId == applicant.UserId));
    }

    // Persists a workspace-scoped "Member" role carrying exactly the given permission codes (none for an empty one).
    private async Task<Guid> SeedMemberRoleAsync(Guid workspaceId, params string[] permissionCodes)
    {
        var role = new Role(Guid.NewGuid(), "Member", RoleScope.Workspace, workspaceId, DateTimeOffset.UtcNow);

        await using var context = _fixture.CreateContext();
        context.Roles.Add(role);

        foreach (var code in permissionCodes)
        {
            var permission = await context.Permissions.FirstOrDefaultAsync(candidate => candidate.Code == code);

            if (permission is null)
            {
                permission = new Permission(Guid.NewGuid(), code);
                context.Permissions.Add(permission);
            }

            context.RolePermissions.Add(new RolePermission(role.Id, permission.Id));
        }

        await context.SaveChangesAsync();

        return role.Id;
    }

    // Another member of the workspace who already holds the role.
    private async Task SeedHolderAsync(Guid workspaceId, Guid roleId)
    {
        using var client = _factory.CreateClient();
        var holder = await _scenario.RegisterActiveUserAsync(client);
        var membership = new WorkspaceMembership(Guid.NewGuid(), holder.UserId, workspaceId, DateTimeOffset.UtcNow);

        await using var context = _fixture.CreateContext();
        context.WorkspaceMemberships.Add(membership);
        context.WorkspaceMembershipRoles.Add(new WorkspaceMembershipRole(membership.Id, roleId));
        await context.SaveChangesAsync();
    }

    private async Task<List<string>> PermissionCodesOfAsync(Guid roleId)
    {
        await using var context = _fixture.CreateContext();

        var codes = await context.RolePermissions
            .Where(candidate => candidate.RoleId == roleId)
            .Join(
                context.Permissions,
                rolePermission => rolePermission.PermissionId,
                permission => permission.Id,
                (_, permission) => permission.Code)
            .ToListAsync();

        return codes.Order(StringComparer.Ordinal).ToList();
    }

    private void AllowReview()
    {
        _factory.PermissionChecker.AllowedPermissionCodes.Add(PermissionCodes.WorkspaceAccessRequestsReview);
    }

    /// <summary>A freshly registered Pending applicant with a pending request in its own workspace.</summary>
    private async Task<RegisteredUser> RegisterApplicantAsync()
    {
        var workspaceId = await _scenario.CreateWorkspaceAsync();
        using var client = _factory.CreateClient();

        return await _scenario.RegisterAsync(client, workspaceId);
    }

    /// <summary>
    /// An Active user id to authenticate as. Whether it holds the permission is decided by the substituted checker,
    /// not by any row, which is what keeps these tests about the endpoints.
    /// </summary>
    private async Task<Guid> CreateReviewerAsync()
    {
        using var client = _factory.CreateClient();
        var reviewer = await _scenario.RegisterActiveUserAsync(client);

        return reviewer.UserId;
    }

    private async Task<HttpClient> CreateReviewerClientAsync()
    {
        var reviewerId = await CreateReviewerAsync();

        return AuthScenario.Authenticate(_factory.CreateClient(), TestJwt.CreateAccessToken(reviewerId));
    }

    private static async Task<ApiErrorResponse> ReadErrorAsync(HttpResponseMessage response)
    {
        return (await response.Content.ReadFromJsonAsync<ApiErrorResponse>())!;
    }

    private sealed class ConcurrencyLosingUnitOfWork : IUnitOfWork
    {
        public void DiscardTrackedChanges()
        {
        }

        public Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromException<int>(new ConcurrencyConflictException(
                PersistenceResource.WorkspaceAccessRequest,
                new InvalidOperationException("Simulated stale review at the persistence boundary.")));
        }
    }
}
