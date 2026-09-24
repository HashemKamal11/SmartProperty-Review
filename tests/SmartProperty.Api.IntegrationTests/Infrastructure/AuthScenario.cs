using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using SmartProperty.Api.Contracts.Authentication;
using SmartProperty.Domain.Workspaces;

namespace SmartProperty.Api.IntegrationTests.Infrastructure;

/// <summary>
/// Arrangement for the authentication endpoint tests, on the collection's shared database.
/// </summary>
/// <remarks>
/// Every value this produces is unique to the calling test — workspace, email, and refresh token — so the tests
/// are order-independent on a database they share and never observe another test's rows. Nothing here asserts,
/// and nothing here reaches around the API: registration and login go through the real HTTP endpoints, and the
/// only direct database writes are the two things no endpoint can do yet. Creating a workspace has no API, and
/// activating a user is the workspace-approval workflow that does not exist yet, so both are done through the
/// domain model on a test context rather than by inserting rows.
/// </remarks>
internal sealed class AuthScenario(ApiPostgreSqlFixture fixture)
{
    /// <summary>TEST-ONLY password. Satisfies the registration and login validators and nothing else uses it.</summary>
    public const string Password = "test-only-password";

    public static string UniqueEmail(string prefix)
    {
        // .invalid is reserved by RFC 2606, so no address produced here can ever belong to anyone.
        return $"{prefix}-{Guid.NewGuid():n}@smartproperty.invalid";
    }

    /// <summary>An opaque value shaped like a refresh token that was never issued by this host.</summary>
    public static string UniqueRefreshToken()
    {
        return Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
    }

    /// <summary>
    /// SHA-256 hex, computed here rather than through <c>ITokenProvider</c> on purpose: a test that asserted the
    /// stored hash using the production hash function would keep passing if that function changed. This pins the
    /// storage format independently.
    /// </summary>
    public static string HashRefreshToken(string refreshToken)
    {
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(refreshToken)));
    }

    public async Task<Guid> CreateWorkspaceAsync()
    {
        var workspace = new Workspace(Guid.NewGuid(), $"Workspace {Guid.NewGuid():n}", DateTimeOffset.UtcNow);

        await using var context = fixture.CreateContext();
        context.Workspaces.Add(workspace);
        await context.SaveChangesAsync();

        return workspace.Id;
    }

    /// <summary>Posts a registration and hands back the raw response, for tests that assert a failure.</summary>
    public static Task<HttpResponseMessage> PostRegisterAsync(HttpClient client, string email, Guid workspaceId)
    {
        return client.PostAsJsonAsync(
            "/api/auth/register",
            new RegisterRequest(email, Password, "Test", "User", workspaceId));
    }

    /// <summary>Registers a new user through the API and requires it to have succeeded.</summary>
    public async Task<RegisteredUser> RegisterAsync(HttpClient client, Guid workspaceId, string? email = null)
    {
        email ??= UniqueEmail("user");

        using var response = await PostRegisterAsync(client, email, workspaceId);
        response.EnsureSuccessStatusCode();

        var body = (await response.Content.ReadFromJsonAsync<RegisterResponse>())!;

        return new RegisteredUser(body.UserId, email, workspaceId);
    }

    /// <summary>
    /// A registered user promoted to Active. Registration deliberately produces a Pending user and the approval
    /// workflow that would activate one is not built yet, so this calls the same domain method that workflow will.
    /// </summary>
    public async Task<RegisteredUser> RegisterActiveUserAsync(HttpClient client)
    {
        var workspaceId = await CreateWorkspaceAsync();
        var user = await RegisterAsync(client, workspaceId);

        await UpdateUserAsync(user.UserId, (candidate, now) => candidate.Activate(now));

        return user;
    }

    /// <summary>Applies a domain state change to one user on a test context.</summary>
    public async Task UpdateUserAsync(Guid userId, Action<Domain.Identity.User, DateTimeOffset> change)
    {
        ArgumentNullException.ThrowIfNull(change);

        await using var context = fixture.CreateContext();

        var user = await context.Users.SingleAsync(candidate => candidate.Id == userId);
        change(user, DateTimeOffset.UtcNow);

        await context.SaveChangesAsync();
    }

    public static async Task<LoginResponse> LoginAsync(HttpClient client, string email)
    {
        using var response = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest(email, Password));

        // Only the status code reaches the failure message; the body carries tokens.
        response.EnsureSuccessStatusCode();

        return (await response.Content.ReadFromJsonAsync<LoginResponse>())!;
    }

    /// <summary>An Active user that has signed in, with the token pair that login returned.</summary>
    public async Task<SignedInUser> SignInAsync(HttpClient client)
    {
        var user = await RegisterActiveUserAsync(client);
        var tokens = await LoginAsync(client, user.Email);

        return new SignedInUser(user, tokens);
    }

    public static Task<HttpResponseMessage> PostRefreshAsync(HttpClient client, string refreshToken)
    {
        return client.PostAsJsonAsync("/api/auth/refresh", new RefreshRequest(refreshToken));
    }

    public static Task<HttpResponseMessage> PostLogoutAsync(HttpClient client, string refreshToken)
    {
        return client.PostAsJsonAsync("/api/auth/logout", new LogoutRequest(refreshToken));
    }

    /// <summary>A client whose requests carry the given access token, which login issued.</summary>
    public static HttpClient Authenticate(HttpClient client, string accessToken)
    {
        ArgumentNullException.ThrowIfNull(client);

        client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);

        return client;
    }
}

internal sealed record RegisteredUser(Guid UserId, string Email, Guid WorkspaceId);

internal sealed record SignedInUser(RegisteredUser User, LoginResponse Tokens);
