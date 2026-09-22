using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using SmartProperty.Application.Abstractions.Persistence;
using SmartProperty.Application.Abstractions.Persistence.Repositories;
using SmartProperty.Domain.Identity;
using SmartProperty.Persistence.IntegrationTests.Infrastructure;
using Xunit;

namespace SmartProperty.Persistence.IntegrationTests.Persistence;

/// <summary>
/// Verifies, against real PostgreSQL, that the identity-critical unique constraints the configurations declare
/// are actually enforced by the database, and records how each violation reaches the Application layer.
/// </summary>
/// <remarks>
/// Every duplicate is attempted from a context that did not write the original row, so the rejection comes from
/// PostgreSQL rather than from the change tracker.
///
/// Only <c>ux_identity_users_email</c> is translated into a provider-neutral exception today; the others surface
/// as <see cref="DbUpdateException"/> wrapping a <see cref="PostgresException"/>. That asymmetry is current
/// behaviour, and these tests record it rather than change it.
/// </remarks>
public sealed class ConstraintTests(PostgreSqlFixture fixture) : DatabaseTest(fixture)
{
    [Fact]
    public async Task ADuplicateUserEmailIsRejectedByTheDatabase()
    {
        const string Email = "duplicate@example.test";

        await using (var seeding = Host.CreateVerificationContext())
        {
            seeding.Users.Add(AccessGraph.ActiveUser(Email));
            await seeding.SaveChangesAsync();
        }

        await using var writing = Host.CreateVerificationContext();
        writing.Users.Add(AccessGraph.ActiveUser(Email));

        var exception = await Assert.ThrowsAsync<DbUpdateException>(() => writing.SaveChangesAsync());
        var postgres = Assert.IsType<PostgresException>(exception.InnerException);

        Assert.Equal(PostgresErrorCodes.UniqueViolation, postgres.SqlState);
        Assert.Equal("ux_identity_users_email", postgres.ConstraintName);
    }

    [Fact]
    public async Task ADuplicateUserEmailReachesTheApplicationAsAUniqueConstraintViolation()
    {
        const string Email = "translated.duplicate@example.test";

        await using (var seeding = Host.CreateVerificationContext())
        {
            seeding.Users.Add(AccessGraph.ActiveUser(Email));
            await seeding.SaveChangesAsync();
        }

        using var scope = Host.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<IUserRepository>();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

        await users.AddAsync(AccessGraph.ActiveUser(Email));

        var exception = await Assert.ThrowsAsync<UniqueConstraintViolationException>(
            () => unitOfWork.SaveChangesAsync());

        Assert.Equal(PersistenceConstraint.UserEmail, exception.Constraint);

        // The provider exception is kept only as diagnostics, two levels down: the neutral exception wraps the
        // DbUpdateException, which wraps the PostgresException.
        var wrapped = Assert.IsType<DbUpdateException>(exception.InnerException);
        Assert.IsType<PostgresException>(wrapped.InnerException);
    }

    [Fact]
    public async Task ADuplicateRefreshTokenHashIsRejectedByTheDatabase()
    {
        const string TokenHash = "test-token-hash-shared-by-two-rows";

        Guid userId;

        await using (var seeding = Host.CreateVerificationContext())
        {
            var user = AccessGraph.ActiveUser();
            userId = user.Id;

            seeding.Users.Add(user);
            seeding.RefreshTokens.Add(AccessGraph.ActiveRefreshToken(user.Id, TokenHash));
            await seeding.SaveChangesAsync();
        }

        await using var writing = Host.CreateVerificationContext();
        writing.RefreshTokens.Add(AccessGraph.ActiveRefreshToken(userId, TokenHash));

        var exception = await Assert.ThrowsAsync<DbUpdateException>(() => writing.SaveChangesAsync());
        var postgres = Assert.IsType<PostgresException>(exception.InnerException);

        Assert.Equal(PostgresErrorCodes.UniqueViolation, postgres.SqlState);
        Assert.Equal("ux_identity_refresh_tokens_token_hash", postgres.ConstraintName);
    }

    [Fact]
    public async Task ADuplicateRefreshTokenHashIsNotTranslatedIntoANeutralException()
    {
        // Current behaviour, recorded rather than designed here: UnitOfWork recognizes only the user-email
        // constraint, so every other unique violation reaches the caller as the provider exception it was.
        const string TokenHash = "test-token-hash-not-translated";

        Guid userId;

        await using (var seeding = Host.CreateVerificationContext())
        {
            var user = AccessGraph.ActiveUser();
            userId = user.Id;

            seeding.Users.Add(user);
            seeding.RefreshTokens.Add(AccessGraph.ActiveRefreshToken(user.Id, TokenHash));
            await seeding.SaveChangesAsync();
        }

        using var scope = Host.CreateScope();
        var refreshTokens = scope.ServiceProvider.GetRequiredService<IRefreshTokenRepository>();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

        await refreshTokens.AddAsync(AccessGraph.ActiveRefreshToken(userId, TokenHash));

        var exception = await Assert.ThrowsAsync<DbUpdateException>(() => unitOfWork.SaveChangesAsync());

        Assert.IsNotType<UniqueConstraintViolationException>(exception);
        Assert.IsType<PostgresException>(exception.InnerException);
    }

    [Fact]
    public async Task ADuplicatePermissionCodeIsRejectedByTheDatabase()
    {
        const string Code = "property.read";

        await using (var seeding = Host.CreateVerificationContext())
        {
            seeding.Permissions.Add(AccessGraph.NewPermission(Code));
            await seeding.SaveChangesAsync();
        }

        await using var writing = Host.CreateVerificationContext();
        writing.Permissions.Add(AccessGraph.NewPermission(Code));

        var exception = await Assert.ThrowsAsync<DbUpdateException>(() => writing.SaveChangesAsync());
        var postgres = Assert.IsType<PostgresException>(exception.InnerException);

        Assert.Equal(PostgresErrorCodes.UniqueViolation, postgres.SqlState);
        Assert.Equal("ux_identity_permissions_code", postgres.ConstraintName);
    }

    [Fact]
    public async Task PermissionCodesThatDifferOnlyByCaseAreDistinctRows()
    {
        // The unique index is over the stored value, which PostgreSQL compares case-sensitively. Codes are
        // compared exactly everywhere else too, so these must be two rows rather than a conflict.
        await using var context = Host.CreateVerificationContext();

        context.Permissions.Add(AccessGraph.NewPermission("Property.Read"));
        context.Permissions.Add(AccessGraph.NewPermission("property.read"));

        await context.SaveChangesAsync();

        Assert.Equal(2, await context.Permissions.CountAsync());
    }

    [Fact]
    public async Task ASecondMembershipForTheSameUserAndWorkspaceIsRejectedByTheDatabase()
    {
        Guid userId;
        Guid workspaceId;

        await using (var seeding = Host.CreateVerificationContext())
        {
            var user = AccessGraph.ActiveUser();
            var workspace = AccessGraph.NewWorkspace();
            userId = user.Id;
            workspaceId = workspace.Id;

            seeding.Users.Add(user);
            seeding.Workspaces.Add(workspace);
            seeding.WorkspaceMemberships.Add(AccessGraph.NewMembership(user.Id, workspace.Id));
            await seeding.SaveChangesAsync();
        }

        await using var writing = Host.CreateVerificationContext();
        writing.WorkspaceMemberships.Add(AccessGraph.NewMembership(userId, workspaceId));

        var exception = await Assert.ThrowsAsync<DbUpdateException>(() => writing.SaveChangesAsync());
        var postgres = Assert.IsType<PostgresException>(exception.InnerException);

        Assert.Equal(PostgresErrorCodes.UniqueViolation, postgres.SqlState);
        Assert.Equal("ux_identity_workspace_memberships_user_workspace", postgres.ConstraintName);
    }

    [Fact]
    public async Task OneUserCannotHoldTwoCredentialRows()
    {
        // user_credentials is keyed by user_id, so the primary key is what enforces one secret per user.
        Guid userId;

        await using (var seeding = Host.CreateVerificationContext())
        {
            var user = AccessGraph.ActiveUser();
            userId = user.Id;

            seeding.Users.Add(user);
            seeding.UserCredentials.Add(new UserCredential(user.Id, "test-password-hash", TestClock.DefaultNow));
            await seeding.SaveChangesAsync();
        }

        await using var writing = Host.CreateVerificationContext();
        writing.UserCredentials.Add(new UserCredential(userId, "test-password-hash-2", TestClock.DefaultNow));

        var exception = await Assert.ThrowsAsync<DbUpdateException>(() => writing.SaveChangesAsync());
        var postgres = Assert.IsType<PostgresException>(exception.InnerException);

        Assert.Equal(PostgresErrorCodes.UniqueViolation, postgres.SqlState);
    }
}
