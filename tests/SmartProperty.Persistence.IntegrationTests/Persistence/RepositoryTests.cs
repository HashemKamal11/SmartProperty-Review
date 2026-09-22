using Microsoft.Extensions.DependencyInjection;
using SmartProperty.Application.Abstractions.Persistence;
using SmartProperty.Application.Abstractions.Persistence.Repositories;
using SmartProperty.Persistence.IntegrationTests.Infrastructure;
using Xunit;

namespace SmartProperty.Persistence.IntegrationTests.Persistence;

/// <summary>
/// Round-trips the real repository implementations through real PostgreSQL.
/// </summary>
/// <remarks>
/// The repositories are internal to SmartProperty.Persistence and are resolved from the container the same way
/// the API resolves them. Each test writes in one scope and reads in another, so the value asserted came back
/// out of the database rather than out of the first scope's change tracker.
///
/// Only the lookups the authentication and authorization paths actually depend on are covered. There is no
/// mechanical create/read/update/delete pass over every entity.
/// </remarks>
public sealed class RepositoryTests(PostgreSqlFixture fixture) : DatabaseTest(fixture)
{
    [Fact]
    public async Task AUserWrittenInOneScopeIsFoundByIdInAnother()
    {
        var user = AccessGraph.ActiveUser("round.trip@example.test");

        using (var writing = Host.CreateScope())
        {
            await writing.ServiceProvider.GetRequiredService<IUserRepository>().AddAsync(user);
            await writing.ServiceProvider.GetRequiredService<IUnitOfWork>().SaveChangesAsync();
        }

        using var reading = Host.CreateScope();
        var found = await reading.ServiceProvider.GetRequiredService<IUserRepository>().GetByIdAsync(user.Id);

        Assert.NotNull(found);
        Assert.Equal("round.trip@example.test", found.Email);
        Assert.Equal(Domain.Identity.UserStatus.Active, found.Status);
    }

    [Fact]
    public async Task AUserIsFoundByEmailRegardlessOfHowTheCallerCasedOrPaddedIt()
    {
        var user = AccessGraph.ActiveUser("casing.probe@example.test");

        await using (var seeding = Host.CreateVerificationContext())
        {
            seeding.Users.Add(user);
            await seeding.SaveChangesAsync();
        }

        using var scope = Host.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<IUserRepository>();

        var found = await users.GetByEmailAsync("  Casing.Probe@Example.TEST  ");

        Assert.NotNull(found);
        Assert.Equal(user.Id, found.Id);
    }

    [Fact]
    public async Task AnUnknownEmailResolvesToNothing()
    {
        using var scope = Host.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<IUserRepository>();

        Assert.Null(await users.GetByEmailAsync("absent@example.test"));
    }

    [Fact]
    public async Task ARefreshTokenIsFoundByItsStoredHash()
    {
        const string TokenHash = "test-token-hash-lookup";

        var user = AccessGraph.ActiveUser();

        using (var writing = Host.CreateScope())
        {
            await writing.ServiceProvider.GetRequiredService<IUserRepository>().AddAsync(user);
            await writing.ServiceProvider.GetRequiredService<IRefreshTokenRepository>()
                .AddAsync(AccessGraph.ActiveRefreshToken(user.Id, TokenHash));
            await writing.ServiceProvider.GetRequiredService<IUnitOfWork>().SaveChangesAsync();
        }

        using var reading = Host.CreateScope();
        var refreshTokens = reading.ServiceProvider.GetRequiredService<IRefreshTokenRepository>();

        var found = await refreshTokens.GetByTokenHashAsync(TokenHash);

        Assert.NotNull(found);
        Assert.Equal(user.Id, found.UserId);
        Assert.True(found.IsActive(TestClock.DefaultNow));
    }

    [Fact]
    public async Task ARefreshTokenLookupMatchesTheHashExactly()
    {
        const string TokenHash = "test-token-hash-Exact";

        var user = AccessGraph.ActiveUser();

        await using (var seeding = Host.CreateVerificationContext())
        {
            seeding.Users.Add(user);
            seeding.RefreshTokens.Add(AccessGraph.ActiveRefreshToken(user.Id, TokenHash));
            await seeding.SaveChangesAsync();
        }

        using var scope = Host.CreateScope();
        var refreshTokens = scope.ServiceProvider.GetRequiredService<IRefreshTokenRepository>();

        Assert.NotNull(await refreshTokens.GetByTokenHashAsync(TokenHash));
        Assert.Null(await refreshTokens.GetByTokenHashAsync("test-token-hash-exact"));
    }

    [Fact]
    public async Task APermissionIsFoundByItsExactCode()
    {
        await using (var seeding = Host.CreateVerificationContext())
        {
            seeding.Permissions.Add(AccessGraph.NewPermission("Property.Read"));
            await seeding.SaveChangesAsync();
        }

        using var scope = Host.CreateScope();
        var permissions = scope.ServiceProvider.GetRequiredService<IPermissionRepository>();

        Assert.NotNull(await permissions.GetByCodeAsync("Property.Read"));

        // Codes are compared exactly, so a differently cased code is a different permission, not the same one.
        Assert.Null(await permissions.GetByCodeAsync("property.read"));
    }

    [Fact]
    public async Task AMembershipLookupNeverCrossesWorkspaces()
    {
        var user = AccessGraph.ActiveUser();
        var workspaceA = AccessGraph.NewWorkspace("Workspace A");
        var workspaceB = AccessGraph.NewWorkspace("Workspace B");

        await using (var seeding = Host.CreateVerificationContext())
        {
            seeding.Users.Add(user);
            seeding.Workspaces.Add(workspaceA);
            seeding.Workspaces.Add(workspaceB);
            seeding.WorkspaceMemberships.Add(AccessGraph.NewMembership(user.Id, workspaceA.Id));
            await seeding.SaveChangesAsync();
        }

        using var scope = Host.CreateScope();
        var memberships = scope.ServiceProvider.GetRequiredService<IWorkspaceMembershipRepository>();

        Assert.NotNull(await memberships.GetByUserAndWorkspaceAsync(user.Id, workspaceA.Id));
        Assert.Null(await memberships.GetByUserAndWorkspaceAsync(user.Id, workspaceB.Id));
    }
}
