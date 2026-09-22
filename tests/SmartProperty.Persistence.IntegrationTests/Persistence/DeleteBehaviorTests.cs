using Microsoft.EntityFrameworkCore;
using Npgsql;
using SmartProperty.Domain.Identity;
using SmartProperty.Persistence.IntegrationTests.Infrastructure;
using Xunit;

namespace SmartProperty.Persistence.IntegrationTests.Persistence;

/// <summary>
/// Verifies against real PostgreSQL that deleting a principal row cannot silently take identity or access rows
/// with it.
/// </summary>
/// <remarks>
/// Every relationship in the model is configured <c>DeleteBehavior.Restrict</c>, and
/// <c>SchemaTests</c> already asserts that no foreign key was created with a cascading action. These tests cover
/// the few relationships where an accidental cascade would be most damaging — a user losing their sessions, a
/// workspace losing its access rows, a permission disappearing from under the roles that grant it — by actually
/// attempting the delete and letting the database refuse it.
///
/// Each delete is issued from a context that has loaded nothing else, so EF sends a plain DELETE and the
/// rejection comes from the foreign key itself.
/// </remarks>
public sealed class DeleteBehaviorTests(PostgreSqlFixture fixture) : DatabaseTest(fixture)
{
    [Fact]
    public async Task AUserWithARefreshTokenCannotBeDeleted()
    {
        Guid userId;

        await using (var seeding = Host.CreateVerificationContext())
        {
            var user = AccessGraph.ActiveUser();
            userId = user.Id;

            seeding.Users.Add(user);
            seeding.RefreshTokens.Add(AccessGraph.ActiveRefreshToken(user.Id, "test-token-hash-restrict"));
            await seeding.SaveChangesAsync();
        }

        await AssertDeleteIsRefusedAsync<User>(userId);

        await using var verifying = Host.CreateVerificationContext();
        Assert.NotNull(await verifying.Users.FirstOrDefaultAsync(user => user.Id == userId));
        Assert.Single(await verifying.RefreshTokens.ToListAsync());
    }

    [Fact]
    public async Task AWorkspaceWithAMembershipCannotBeDeleted()
    {
        Guid workspaceId;

        await using (var seeding = Host.CreateVerificationContext())
        {
            var user = AccessGraph.ActiveUser();
            var workspace = AccessGraph.NewWorkspace();
            workspaceId = workspace.Id;

            seeding.Users.Add(user);
            seeding.Workspaces.Add(workspace);
            seeding.WorkspaceMemberships.Add(AccessGraph.NewMembership(user.Id, workspace.Id));
            await seeding.SaveChangesAsync();
        }

        await AssertDeleteIsRefusedAsync<Domain.Workspaces.Workspace>(workspaceId);

        await using var verifying = Host.CreateVerificationContext();
        Assert.Single(await verifying.WorkspaceMemberships.ToListAsync());
    }

    [Fact]
    public async Task APermissionGrantedByARoleCannotBeDeleted()
    {
        Guid permissionId;

        await using (var seeding = Host.CreateVerificationContext())
        {
            var (_, permission) = await AccessGraph.SeedPlatformGrantAsync(seeding, "property.read");
            permissionId = permission.Id;
        }

        await AssertDeleteIsRefusedAsync<Permission>(permissionId);

        await using var verifying = Host.CreateVerificationContext();
        Assert.Single(await verifying.RolePermissions.ToListAsync());
    }

    [Fact]
    public async Task ARoleAssignedToAUserCannotBeDeleted()
    {
        Guid roleId;

        await using (var seeding = Host.CreateVerificationContext())
        {
            await AccessGraph.SeedPlatformGrantAsync(seeding, "property.read");
            roleId = await seeding.Roles.Select(role => role.Id).SingleAsync();
        }

        await AssertDeleteIsRefusedAsync<Role>(roleId);

        await using var verifying = Host.CreateVerificationContext();
        Assert.Single(await verifying.UserPlatformRoles.ToListAsync());
    }

    private async Task AssertDeleteIsRefusedAsync<TEntity>(Guid id)
        where TEntity : class
    {
        await using var deleting = Host.CreateVerificationContext();

        var entity = await deleting.Set<TEntity>().FindAsync(id)
            ?? throw new InvalidOperationException($"The seeded {typeof(TEntity).Name} was not found.");

        deleting.Set<TEntity>().Remove(entity);

        var exception = await Assert.ThrowsAsync<DbUpdateException>(() => deleting.SaveChangesAsync());
        var postgres = Assert.IsType<PostgresException>(exception.InnerException);

        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, postgres.SqlState);
    }
}
