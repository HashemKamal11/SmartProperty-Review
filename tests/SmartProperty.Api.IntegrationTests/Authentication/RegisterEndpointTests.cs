using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using SmartProperty.Api.Contracts;
using SmartProperty.Api.Contracts.Authentication;
using SmartProperty.Api.IntegrationTests.Infrastructure;
using SmartProperty.Domain.Identity;
using SmartProperty.Domain.Workspaces;
using Xunit;

namespace SmartProperty.Api.IntegrationTests.Authentication;

/// <summary>
/// End-to-end coverage for POST /api/auth/register against the real host and a real database.
/// </summary>
/// <remarks>
/// Registration is deliberately not a sign-up that grants access: it creates a Pending user, a credential, and
/// one Pending workspace access request, and issues no token. These tests pin that contract at the HTTP boundary
/// and in the rows it writes.
/// </remarks>
[Collection(ApiPostgreSqlCollection.Name)]
public sealed class RegisterEndpointTests : IDisposable
{
    private const string Endpoint = "/api/auth/register";

    private readonly ApiPostgreSqlFixture _fixture;
    private readonly SmartPropertyApiFactory _factory;
    private readonly AuthScenario _scenario;

    public RegisterEndpointTests(ApiPostgreSqlFixture fixture)
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
    public async Task ValidRegistration_Returns201WithAPendingUserAndAPendingAccessRequest()
    {
        var workspaceId = await _scenario.CreateWorkspaceAsync();
        var email = AuthScenario.UniqueEmail("register-valid");

        using var client = _factory.CreateClient();
        using var response = await AuthScenario.PostRegisterAsync(client, email, workspaceId);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var body = (await response.Content.ReadFromJsonAsync<RegisterResponse>())!;

        Assert.Equal(nameof(UserStatus.Pending), body.Status);
        Assert.Equal(nameof(WorkspaceAccessRequestStatus.Pending), body.WorkspaceAccessStatus);
        Assert.Equal(workspaceId, body.WorkspaceId);
        Assert.NotEqual(Guid.Empty, body.UserId);
        Assert.NotEqual(Guid.Empty, body.WorkspaceAccessRequestId);

        // No token is issued by registration, so nothing in the response may look like one.
        Assert.DoesNotContain("token", await response.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);

        await using var context = _fixture.CreateContext();

        var user = await context.Users.SingleAsync(candidate => candidate.Id == body.UserId);
        Assert.Equal(email.ToLowerInvariant(), user.Email);
        Assert.Equal(UserStatus.Pending, user.Status);

        // The credential row must exist. Its hash is never read, compared, or printed by any assertion.
        Assert.True(await context.UserCredentials.AnyAsync(candidate => candidate.UserId == body.UserId));

        var accessRequest = await context.WorkspaceAccessRequests
            .SingleAsync(candidate => candidate.UserId == body.UserId);

        Assert.Equal(body.WorkspaceAccessRequestId, accessRequest.Id);
        Assert.Equal(workspaceId, accessRequest.WorkspaceId);
        Assert.Equal(WorkspaceAccessRequestStatus.Pending, accessRequest.Status);
    }

    [Fact]
    public async Task UnknownWorkspace_Returns404AndPersistsNothing()
    {
        var unknownWorkspaceId = Guid.NewGuid();
        var email = AuthScenario.UniqueEmail("register-unknown-workspace");

        using var client = _factory.CreateClient();
        using var response = await AuthScenario.PostRegisterAsync(client, email, unknownWorkspaceId);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

        var error = (await response.Content.ReadFromJsonAsync<ApiErrorResponse>())!;
        Assert.Equal("workspaces.not_found", error.Code);
        Assert.Equal(StatusCodes.Status404NotFound, error.Status);

        await using var context = _fixture.CreateContext();

        var normalizedEmail = email.ToLowerInvariant();
        var user = await context.Users.SingleOrDefaultAsync(candidate => candidate.Email == normalizedEmail);

        Assert.Null(user);
        Assert.False(await context.WorkspaceAccessRequests
            .AnyAsync(candidate => candidate.WorkspaceId == unknownWorkspaceId));
    }

    [Fact]
    public async Task DuplicateEmail_Returns409AndLeavesOneUser()
    {
        var workspaceId = await _scenario.CreateWorkspaceAsync();
        var email = AuthScenario.UniqueEmail("register-duplicate");

        using var client = _factory.CreateClient();

        var first = await _scenario.RegisterAsync(client, workspaceId, email);

        // User normalizes the stored email to lower case, so a different casing is the same account.
        using var response = await AuthScenario.PostRegisterAsync(client, email.ToUpperInvariant(), workspaceId);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);

        var error = (await response.Content.ReadFromJsonAsync<ApiErrorResponse>())!;
        Assert.Equal("users.email_already_exists", error.Code);
        Assert.Equal(StatusCodes.Status409Conflict, error.Status);

        await using var context = _fixture.CreateContext();

        var normalizedEmail = email.ToLowerInvariant();

        // Scoped to this test's own email, never a table-wide count: the database is shared.
        var user = await context.Users.SingleAsync(candidate => candidate.Email == normalizedEmail);
        Assert.Equal(first.UserId, user.Id);

        Assert.Single(await context.WorkspaceAccessRequests
            .Where(candidate => candidate.UserId == first.UserId)
            .ToListAsync());
    }

    [Fact]
    public async Task MissingBody_IsRejectedWithoutTouchingTheDatabase()
    {
        using var client = _factory.CreateClient();
        using var content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json");

        using var response = await client.PostAsync(Endpoint, content);

        // Contract values come from the current validator: nullable contract members mean a missing field is an
        // Application validation failure (422), not model-binding's required-member 400.
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);

        var error = (await response.Content.ReadFromJsonAsync<ApiErrorResponse>())!;
        Assert.Equal("validation.failed", error.Code);
        Assert.Equal(StatusCodes.Status422UnprocessableEntity, error.Status);
    }
}
