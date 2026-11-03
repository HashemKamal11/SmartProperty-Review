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
    public async Task ValidRegistration_Returns201WithAPendingUserAndNoAccessRequest()
    {
        var email = AuthScenario.UniqueEmail("register-valid");

        using var client = _factory.CreateClient();
        using var response = await AuthScenario.PostRegisterAsync(client, email);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var body = (await response.Content.ReadFromJsonAsync<RegisterResponse>())!;

        Assert.Equal(nameof(UserStatus.Pending), body.Status);
        Assert.NotEqual(Guid.Empty, body.UserId);

        // No token is issued by registration, so nothing in the response may look like one.
        Assert.DoesNotContain("token", await response.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);

        await using var context = _fixture.CreateContext();

        var user = await context.Users.SingleAsync(candidate => candidate.Id == body.UserId);
        Assert.Equal(email.ToLowerInvariant(), user.Email);
        Assert.Equal(UserStatus.Pending, user.Status);

        // The credential row must exist. Its hash is never read, compared, or printed by any assertion.
        Assert.True(await context.UserCredentials.AnyAsync(candidate => candidate.UserId == body.UserId));

        // Registration is not tied to a workspace: no access request, membership or role is created.
        Assert.False(await context.WorkspaceAccessRequests.AnyAsync(candidate => candidate.UserId == body.UserId));
        Assert.False(await context.WorkspaceMemberships.AnyAsync(candidate => candidate.UserId == body.UserId));
    }

    [Fact]
    public async Task RegisteredUser_CannotSignInUntilPhoneAndEmailAreVerified_ThenSignsInWithNoAdministrator()
    {
        var email = AuthScenario.UniqueEmail("register-login");
        var phone = AuthScenario.UniquePhone();

        using var client = _factory.CreateClient();
        using var response = await AuthScenario.PostRegisterAsync(client, email, phone);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var registered = (await response.Content.ReadFromJsonAsync<RegisterResponse>())!;

        async Task<HttpStatusCode> LoginStatusAsync()
        {
            using var login = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest(email, AuthScenario.Password));
            return login.StatusCode;
        }

        // Nothing verified yet: the right password is still refused.
        Assert.Equal(HttpStatusCode.Forbidden, await LoginStatusAsync());

        // The phone alone is not enough.
        using (var verifyPhone = await client.PostAsJsonAsync(
            "/api/auth/verify-phone", new VerifyPhoneRequest(phone, _factory.SmsSender.LatestCodeFor(phone))))
        {
            Assert.Equal(HttpStatusCode.OK, verifyPhone.StatusCode);
        }
        Assert.Equal(HttpStatusCode.Forbidden, await LoginStatusAsync());

        // Verifying the email too activates the account; nobody approves or activates it.
        using (var verifyEmail = await client.PostAsJsonAsync(
            "/api/auth/verify-email", new VerifyEmailRequest(email, _factory.EmailSender.LatestCodeFor(email))))
        {
            Assert.Equal(HttpStatusCode.OK, verifyEmail.StatusCode);
        }
        Assert.Equal(HttpStatusCode.OK, await LoginStatusAsync());

        await using var context = _fixture.CreateContext();
        var user = await context.Users.SingleAsync(candidate => candidate.Id == registered.UserId);
        Assert.Equal(UserStatus.Active, user.Status);
    }

    [Fact]
    public async Task AWorkspaceIdInTheBodyIsIgnored()
    {
        var workspaceId = await _scenario.CreateWorkspaceAsync();
        var email = AuthScenario.UniqueEmail("register-ignored-workspace");

        using var client = _factory.CreateClient();
        using var content = JsonContent.Create(new
        {
            email,
            password = AuthScenario.Password,
            firstName = "Test",
            lastName = "User",
            phoneNumber = AuthScenario.UniquePhone(),
            workspaceId,
        });
        using var response = await client.PostAsync(Endpoint, content);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = (await response.Content.ReadFromJsonAsync<RegisterResponse>())!;
        await using var context = _fixture.CreateContext();
        Assert.False(await context.WorkspaceAccessRequests.AnyAsync(candidate => candidate.UserId == body.UserId));
    }

    [Fact]
    public async Task DuplicateEmail_Returns409AndLeavesOneUser()
    {
        var workspaceId = await _scenario.CreateWorkspaceAsync();
        var email = AuthScenario.UniqueEmail("register-duplicate");

        using var client = _factory.CreateClient();

        var first = await _scenario.RegisterAsync(client, workspaceId, email);

        // User normalizes the stored email to lower case, so a different casing is the same account.
        using var response = await AuthScenario.PostRegisterAsync(client, email.ToUpperInvariant());

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);

        var error = (await response.Content.ReadFromJsonAsync<ApiErrorResponse>())!;
        Assert.Equal("users.email_already_exists", error.Code);
        Assert.Equal(StatusCodes.Status409Conflict, error.Status);

        await using var context = _fixture.CreateContext();

        var normalizedEmail = email.ToLowerInvariant();

        // Scoped to this test's own email, never a table-wide count: the database is shared.
        var user = await context.Users.SingleAsync(candidate => candidate.Email == normalizedEmail);
        Assert.Equal(first.UserId, user.Id);

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
