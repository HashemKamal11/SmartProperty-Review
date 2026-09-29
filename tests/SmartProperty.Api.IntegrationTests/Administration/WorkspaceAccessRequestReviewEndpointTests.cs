using System.Net;
using System.Net.Http.Json;
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
    public async Task AfterApprovalTheDatabaseHoldsTheDecisionTheActivationAndTheMembershipAndNoRole()
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

        // Approval grants membership only. No workspace role is assigned, silently or otherwise.
        Assert.False(await context.WorkspaceMembershipRoles
            .AnyAsync(candidate => candidate.WorkspaceMembershipId == membership.Id));
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
