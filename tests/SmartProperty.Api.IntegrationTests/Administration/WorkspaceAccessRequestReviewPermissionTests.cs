using System.Net;
using System.Net.Http.Json;
using SmartProperty.Api.Contracts;
using SmartProperty.Api.Contracts.Administration;
using SmartProperty.Api.Infrastructure.Errors;
using SmartProperty.Api.IntegrationTests.Infrastructure;
using SmartProperty.Application.Authorization;
using Xunit;

namespace SmartProperty.Api.IntegrationTests.Administration;

/// <summary>
/// The review endpoints against the <i>production</i> permission checker and real rows: who may actually review,
/// and what kind of grant is allowed to say yes.
/// </summary>
/// <remarks>
/// Nothing is substituted in this host — no fake checker, no fake authentication. The answers come from the same
/// PostgreSQL access graph <c>PermissionChecker</c> queries in production, so a mistake in the scope rules would
/// surface here rather than being hidden behind a test double.
/// </remarks>
[Collection(ApiPostgreSqlCollection.Name)]
public sealed class WorkspaceAccessRequestReviewPermissionTests : IDisposable
{
    private const string BaseRoute = "/api/admin/workspace-access-requests";

    private readonly SmartPropertyApiFactory _factory;
    private readonly AuthScenario _scenario;

    public WorkspaceAccessRequestReviewPermissionTests(ApiPostgreSqlFixture fixture)
    {
        _factory = new SmartPropertyApiFactory(fixture, usePersistencePermissionChecker: true);
        _scenario = new AuthScenario(fixture);
    }

    public void Dispose()
    {
        _factory.Dispose();
    }

    [Fact]
    public async Task AnActiveUserHoldingNoRole_Returns403()
    {
        var applicant = await RegisterApplicantAsync();
        var reviewerId = await CreateActiveUserAsync();
        using var client = Authenticated(reviewerId);

        var response = await client.PostAsync($"{BaseRoute}/{applicant.WorkspaceAccessRequestId}/approve", null);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(ApiErrorCodes.Forbidden, (await ReadErrorAsync(response)).Code);
    }

    [Fact]
    public async Task AUserHoldingTheReviewPermissionAtPlatformScope_CanApprove()
    {
        var applicant = await RegisterApplicantAsync();
        var reviewerId = await CreateActiveUserAsync();
        await _scenario.GrantPlatformPermissionAsync(
            reviewerId,
            PermissionCodes.WorkspaceAccessRequestsReview);

        using var client = Authenticated(reviewerId);

        var response = await client.PostAsync($"{BaseRoute}/{applicant.WorkspaceAccessRequestId}/approve", null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = (await response.Content.ReadFromJsonAsync<ApproveWorkspaceAccessRequestResponse>())!;
        Assert.Equal(applicant.UserId, body.UserId);
        Assert.NotEqual(Guid.Empty, body.MembershipId);
    }

    [Fact]
    public async Task TheSameGrantHeldOnlyInsideAWorkspace_Returns403()
    {
        var applicant = await RegisterApplicantAsync();
        var reviewerId = await CreateActiveUserAsync();

        // The identical permission code, reachable only through a workspace membership and a workspace role.
        await _scenario.GrantWorkspacePermissionAsync(
            reviewerId,
            applicant.WorkspaceId,
            PermissionCodes.WorkspaceAccessRequestsReview);

        using var client = Authenticated(reviewerId);

        var response = await client.PostAsync($"{BaseRoute}/{applicant.WorkspaceAccessRequestId}/approve", null);

        // Reviewing access requests is a platform act. Nothing held inside a workspace may satisfy it, not even
        // inside the very workspace the request is for.
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(ApiErrorCodes.Forbidden, (await ReadErrorAsync(response)).Code);
    }

    [Fact]
    public async Task APendingUserHoldingThePlatformGrant_IsStillRefused()
    {
        var applicant = await RegisterApplicantAsync();
        var workspaceId = await _scenario.CreateWorkspaceAsync();

        using (var registrationClient = _factory.CreateClient())
        {
            var pendingReviewer = await _scenario.RegisterAsync(registrationClient, workspaceId);
            await _scenario.GrantPlatformPermissionAsync(
                pendingReviewer.UserId,
                PermissionCodes.WorkspaceAccessRequestsReview);

            using var client = Authenticated(pendingReviewer.UserId);

            var response = await client.PostAsync(
                $"{BaseRoute}/{applicant.WorkspaceAccessRequestId}/approve",
                null);

            // The active-user invariant is checked against the database, not inferred from the token.
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            Assert.Equal(ApiErrorCodes.Forbidden, (await ReadErrorAsync(response)).Code);
        }
    }

    [Fact]
    public async Task AUserHoldingThePlatformGrant_CanReadTheQueue()
    {
        var applicant = await RegisterApplicantAsync();
        var reviewerId = await CreateActiveUserAsync();
        await _scenario.GrantPlatformPermissionAsync(
            reviewerId,
            PermissionCodes.WorkspaceAccessRequestsReview);

        using var client = Authenticated(reviewerId);

        var body = (await client.GetFromJsonAsync<PagedResponse<WorkspaceAccessRequestResponse>>(
            $"{BaseRoute}?workspaceId={applicant.WorkspaceId}"))!;

        Assert.Equal(applicant.WorkspaceAccessRequestId, Assert.Single(body.Items).RequestId);
    }

    [Fact]
    public async Task AnActiveUserHoldingNoRole_CannotReadTheQueue()
    {
        var reviewerId = await CreateActiveUserAsync();
        using var client = Authenticated(reviewerId);

        var response = await client.GetAsync(BaseRoute);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(ApiErrorCodes.Forbidden, (await ReadErrorAsync(response)).Code);
    }

    private HttpClient Authenticated(Guid userId)
    {
        return AuthScenario.Authenticate(_factory.CreateClient(), TestJwt.CreateAccessToken(userId));
    }

    private async Task<RegisteredUser> RegisterApplicantAsync()
    {
        var workspaceId = await _scenario.CreateWorkspaceAsync();
        using var client = _factory.CreateClient();

        return await _scenario.RegisterAsync(client, workspaceId);
    }

    private async Task<Guid> CreateActiveUserAsync()
    {
        using var client = _factory.CreateClient();
        var user = await _scenario.RegisterActiveUserAsync(client);

        return user.UserId;
    }

    private static async Task<ApiErrorResponse> ReadErrorAsync(HttpResponseMessage response)
    {
        return (await response.Content.ReadFromJsonAsync<ApiErrorResponse>())!;
    }
}
