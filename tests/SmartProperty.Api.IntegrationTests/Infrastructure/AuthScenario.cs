using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using SmartProperty.Api.Contracts.Authentication;
using SmartProperty.Domain.Workspaces;
using SmartProperty.Persistence.Context;

namespace SmartProperty.Api.IntegrationTests.Infrastructure;

/// <summary>
/// Arrangement for the authentication endpoint tests, on the collection's shared database.
/// </summary>
/// <remarks>
/// Every value this produces is unique to the calling test — workspace, email, and refresh token — so the tests
/// are order-independent on a database they share and never observe another test's rows. Nothing here asserts,
/// and nothing here reaches around the API: registration and login go through the real HTTP endpoints, and the
/// only direct database writes are the things an authentication test must not route through another feature.
/// Creating a workspace has no API, and activating a user is what the workspace approval endpoint does — using
/// that endpoint here would make every authentication test depend on it — so both are done through the
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

    /// <summary>A syntactically valid, unique international number. Digits are random, so it is not a real line.</summary>
    public static string UniquePhone()
    {
        return $"+1555{RandomNumberGenerator.GetInt32(0, 10_000_000):D7}";
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
    public static Task<HttpResponseMessage> PostRegisterAsync(
        HttpClient client,
        string email,
        Guid workspaceId,
        string? phoneNumber = null)
    {
        return client.PostAsJsonAsync(
            "/api/auth/register",
            new RegisterRequest(email, Password, "Test", "User", phoneNumber ?? UniquePhone(), workspaceId));
    }

    /// <summary>
    /// Registers a new user through the API and requires it to have succeeded. The phone number is marked verified
    /// through the domain model, like activation below, so a test that is not about phone verification is not
    /// blocked by it; pass <paramref name="verifyPhone"/> false to leave it unverified.
    /// </summary>
    public async Task<RegisteredUser> RegisterAsync(
        HttpClient client,
        Guid workspaceId,
        string? email = null,
        bool verifyPhone = true)
    {
        email ??= UniqueEmail("user");
        var phoneNumber = UniquePhone();

        using var response = await PostRegisterAsync(client, email, workspaceId, phoneNumber);
        response.EnsureSuccessStatusCode();

        var body = (await response.Content.ReadFromJsonAsync<RegisterResponse>())!;

        if (verifyPhone)
        {
            await UpdateUserAsync(body.UserId, (candidate, now) => candidate.VerifyPhone(now));
        }

        return new RegisteredUser(body.UserId, email, workspaceId, body.WorkspaceAccessRequestId, phoneNumber);
    }

    /// <summary>
    /// A registered user promoted to Active. Registration deliberately produces a Pending user, and this calls the
    /// same domain method the approval workflow calls rather than going through that endpoint — an authentication
    /// test must not fail because workspace approval broke.
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

    /// <summary>
    /// Persists a complete platform grant for an existing user: a platform-scoped role carrying
    /// <paramref name="permissionCode"/>, assigned to that user.
    /// </summary>
    /// <remarks>
    /// Written through the domain constructors on a test context, because nothing but the bootstrap mechanism can
    /// create a platform role assignment, and a test that wants to prove the bootstrap works must not be the thing
    /// that arranged the grant. Used by the tests that run against the real persistence-backed permission checker.
    /// </remarks>
    public async Task GrantPlatformPermissionAsync(Guid userId, string permissionCode)
    {
        await using var context = fixture.CreateContext();

        var role = new Domain.Identity.Role(
            Guid.NewGuid(),
            $"Test Platform Role {Guid.NewGuid():n}",
            Domain.Identity.RoleScope.Platform,
            workspaceId: null,
            DateTimeOffset.UtcNow);

        var permission = await EnsurePermissionAsync(context, permissionCode);

        context.Roles.Add(role);
        context.RolePermissions.Add(new Domain.Identity.RolePermission(role.Id, permission.Id));
        context.UserPlatformRoles.Add(new Domain.Identity.UserPlatformRole(userId, role.Id));

        await context.SaveChangesAsync();
    }

    /// <summary>
    /// Persists the same permission as a <i>workspace</i> grant instead: a membership plus a workspace-scoped role
    /// carrying it. Nothing about this may satisfy a platform target, which is what the test using it asserts.
    /// </summary>
    public async Task GrantWorkspacePermissionAsync(Guid userId, Guid workspaceId, string permissionCode)
    {
        await using var context = fixture.CreateContext();

        var membership = new WorkspaceMembership(Guid.NewGuid(), userId, workspaceId, DateTimeOffset.UtcNow);
        var role = new Domain.Identity.Role(
            Guid.NewGuid(),
            $"Test Workspace Role {Guid.NewGuid():n}",
            Domain.Identity.RoleScope.Workspace,
            workspaceId,
            DateTimeOffset.UtcNow);

        var permission = await EnsurePermissionAsync(context, permissionCode);

        context.WorkspaceMemberships.Add(membership);
        context.Roles.Add(role);
        context.RolePermissions.Add(new Domain.Identity.RolePermission(role.Id, permission.Id));
        context.WorkspaceMembershipRoles.Add(
            new Domain.Workspaces.WorkspaceMembershipRole(membership.Id, role.Id));

        await context.SaveChangesAsync();
    }

    /// <summary>
    /// The permission row for this code, created if the shared test database does not have it yet. The code is
    /// unique, and every test in the collection runs against one database, so it may already exist.
    /// </summary>
    private static async Task<Domain.Identity.Permission> EnsurePermissionAsync(
        ApplicationDbContext context,
        string permissionCode)
    {
        var existing = await context.Permissions
            .FirstOrDefaultAsync(permission => permission.Code == permissionCode);

        if (existing is not null)
        {
            return existing;
        }

        var permission = new Domain.Identity.Permission(Guid.NewGuid(), permissionCode);
        context.Permissions.Add(permission);

        return permission;
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

internal sealed record RegisteredUser(
    Guid UserId,
    string Email,
    Guid WorkspaceId,
    Guid WorkspaceAccessRequestId,
    string PhoneNumber);

internal sealed record SignedInUser(RegisteredUser User, LoginResponse Tokens);
