using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using SmartProperty.Application.Abstractions.Identity;
using SmartProperty.Application.Abstractions.Persistence.Repositories;
using SmartProperty.Application.WorkspaceAccessRequests.Approve;
using SmartProperty.Application.WorkspaceAccessRequests.Reject;
using SmartProperty.Common.Results;
using SmartProperty.Domain.Identity;
using SmartProperty.Domain.Workspaces;
using SmartProperty.Persistence.Context;
using SmartProperty.Persistence.IntegrationTests.Infrastructure;
using Xunit;
using Xunit.Abstractions;

namespace SmartProperty.Persistence.IntegrationTests.Concurrency;

public sealed class WorkspaceApprovalGapRegressionTests(PostgreSqlFixture fixture, ITestOutputHelper output)
    : DatabaseTest(fixture)
{
    private readonly P008Checkpoint lookup = new();
    private readonly P008Checkpoint save = new();
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(30);

    private protected override IInterceptor[] ExtraInterceptors() => [new P008BeforeSaveBarrier(save)];

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NewlyVisibleMemberRoleMustRemainProtectedThroughCommit(bool addExtraPermission)
    {
        var seed = await SeedAsync(rolePresent: false);
        using var scope = Host.CreateScope();
        var roles = P008RepositoryReadBarrier<IRoleRepository, Role?>.Wrap(
            scope.ServiceProvider.GetRequiredService<IRoleRepository>(),
            nameof(IRoleRepository.GetWorkspaceRoleByNameAsync), lookup);
        var approval = Approve(scope, seed, roles);
        try
        {
            await ReachLookupAsync(approval);
            var beforeCreate = await SnapshotAsync();
            var created = await TryWriteAsync(async database =>
            {
                database.Roles.Add(new Role(seed.RoleId, "Member", RoleScope.Workspace, seed.Workspace.Id, TestClock.DefaultNow));
                database.RolePermissions.Add(new(seed.RoleId, seed.ReadId));
                await database.SaveChangesAsync();
            });
            output.WriteLine($"Role insertion committed before lookup: {created}");
            if (!created) Assert.Equal(beforeCreate, await SnapshotAsync());
            var afterCreate = await SnapshotAsync();
            lookup.Release();
            if (!await ReachSaveAsync(approval))
            {
                var refused = await approval;
                Assert.False(refused.IsSuccess);
                Assert.Equal(afterCreate, await SnapshotAsync());
                await AssertPendingAsync(seed);
                return;
            }

            var beforeMutation = await SnapshotAsync();
            var mutated = created && addExtraPermission && await TryWriteAsync(async database =>
            {
                database.RolePermissions.Add(new(seed.RoleId, seed.ExtraId));
                await database.SaveChangesAsync();
            });
            output.WriteLine($"Extra permission committed before approval save: {mutated}");
            if (!mutated) Assert.Equal(beforeMutation, await SnapshotAsync());
            var afterWriter = await SnapshotAsync();
            save.Release();
            var result = await approval.WaitAsync(Deadline);
            if (mutated)
            {
                Assert.False(result.IsSuccess);
                Assert.Equal(afterWriter, await SnapshotAsync());
                await AssertPendingAsync(seed);
                await AssertRoleAsync(seed, ["p008.gap.extra", "workspace.read"], assignments: 0);
            }
            else
            {
                Assert.True(result.IsSuccess);
                await AssertApprovedAsync(seed, created ? seed.RoleId : null);
            }
        }
        finally { await DrainAsync(approval); }
    }

    [Fact]
    public async Task NewlyVisibleWorkspaceReadCannotBeRenamedBeforeApprovalCommits()
    {
        var seed = await SeedAsync(readPresent: false);
        using var scope = Host.CreateScope();
        var permissions = P008RepositoryReadBarrier<IPermissionRepository, Permission?>.Wrap(
            scope.ServiceProvider.GetRequiredService<IPermissionRepository>(),
            nameof(IPermissionRepository.GetByCodeAsync), lookup);
        var approval = Approve(scope, seed, permissions);
        try
        {
            await ReachLookupAsync(approval);
            var beforeCreate = await SnapshotAsync();
            var created = await TryWriteAsync(async database =>
            {
                database.Permissions.Add(new Permission(seed.ReadId, "workspace.read"));
                await database.SaveChangesAsync();
            });
            output.WriteLine($"Permission insertion committed before lookup: {created}");
            if (!created) Assert.Equal(beforeCreate, await SnapshotAsync());
            var afterCreate = await SnapshotAsync();
            lookup.Release();
            if (!await ReachSaveAsync(approval))
            {
                Assert.False((await approval).IsSuccess);
                Assert.Equal(afterCreate, await SnapshotAsync());
                await AssertPendingAsync(seed);
                return;
            }
            var renamed = created && await TryWriteAsync(async database =>
            {
                var affected = await database.Database.ExecuteSqlInterpolatedAsync(
                    $"UPDATE identity.permissions SET code = 'p008.gap.renamed' WHERE id = {seed.ReadId}");
                Assert.Equal(1, affected);
            });
            output.WriteLine($"Permission rename committed before approval save: {renamed}");
            if (!renamed) Assert.Equal(afterCreate, await SnapshotAsync());
            var afterWriter = await SnapshotAsync();
            save.Release();
            var result = await approval.WaitAsync(Deadline);
            if (renamed)
            {
                Assert.False(result.IsSuccess);
                Assert.Equal(afterWriter, await SnapshotAsync());
                await AssertPendingAsync(seed);
                await AssertRoleAsync(seed, [], assignments: 0);
            }
            else
            {
                Assert.True(result.IsSuccess);
                await AssertApprovedAsync(seed, seed.RoleId);
            }
        }
        finally { await DrainAsync(approval); }
    }

    [Fact]
    public async Task ExistingMemberAssignmentCannotDisappearBetweenValidationAndCommit()
    {
        var seed = await SeedAsync(assigned: true);
        using var scope = Host.CreateScope();
        var approval = Approve(scope, seed);
        try
        {
            Assert.True(await ReachSaveAsync(approval));
            var beforeWriter = await SnapshotAsync();
            var deleted = await TryWriteAsync(async database =>
            {
                var affected = await database.Database.ExecuteSqlInterpolatedAsync(
                    $"DELETE FROM identity.workspace_membership_roles WHERE workspace_membership_id = {seed.MembershipId} AND role_id = {seed.RoleId}");
                Assert.Equal(1, affected);
            });
            output.WriteLine($"Assignment deletion committed before approval save: {deleted}");
            if (!deleted) Assert.Equal(beforeWriter, await SnapshotAsync());
            var afterWriter = await SnapshotAsync();
            save.Release();
            var result = await approval.WaitAsync(Deadline);
            if (result.IsSuccess)
                await AssertApprovedAsync(seed, seed.RoleId);
            else
            {
                Assert.True(deleted);
                Assert.Equal(afterWriter, await SnapshotAsync());
                await AssertPendingAsync(seed);
                await AssertRoleAsync(seed, ["workspace.read"], assignments: 0);
            }
        }
        finally { await DrainAsync(approval); }
    }

    [Fact]
    public async Task RejectRacingPausedApprovalPreservesExactlyOneDecisionAndAtomicEffects()
    {
        var seed = await SeedAsync();
        using var approvalScope = Host.CreateScope();
        using var rejectionScope = Host.CreateScope();
        var approval = Approve(approvalScope, seed);
        try
        {
            Assert.True(await ReachSaveAsync(approval));
            var beforeWriter = await SnapshotAsync();
            var rejecting = ActivatorUtilities.CreateInstance<RejectWorkspaceAccessRequestCommandHandler>(
                rejectionScope.ServiceProvider, new CurrentUser(seed.RejectReviewer.Id));
            var rejected = await TryWriteContextAsync(
                rejectionScope.ServiceProvider.GetRequiredService<ApplicationDbContext>(),
                async () => Assert.True((await rejecting.Handle(new(seed.Request.Id))).IsSuccess));
            output.WriteLine($"Rejection committed before approval save: {rejected}");
            if (!rejected) Assert.Equal(beforeWriter, await SnapshotAsync());
            var afterWriter = await SnapshotAsync();
            save.Release();
            var result = await approval.WaitAsync(Deadline);
            if (rejected)
            {
                Assert.False(result.IsSuccess);
                Assert.Equal(afterWriter, await SnapshotAsync());
                await AssertRequestAsync(seed, WorkspaceAccessRequestStatus.Rejected, UserStatus.Pending, seed.RejectReviewer.Id);
                await AssertRoleAsync(seed, ["workspace.read"], assignments: 0);
            }
            else
            {
                Assert.True(result.IsSuccess);
                await AssertApprovedAsync(seed, seed.RoleId);
                using var laterScope = Host.CreateScope();
                var later = ActivatorUtilities.CreateInstance<RejectWorkspaceAccessRequestCommandHandler>(
                    laterScope.ServiceProvider, new CurrentUser(seed.RejectReviewer.Id));
                var beforeLater = await SnapshotAsync();
                Assert.False((await later.Handle(new(seed.Request.Id))).IsSuccess);
                Assert.Equal(beforeLater, await SnapshotAsync());
            }
        }
        finally { await DrainAsync(approval); }
    }

    [Fact]
    public async Task ApplicantSuspensionMustSerializeOrPreventAllApprovalSideEffects()
    {
        var seed = await SeedAsync();
        using var scope = Host.CreateScope();
        var approval = Approve(scope, seed);
        try
        {
            Assert.True(await ReachSaveAsync(approval));
            var beforeWriter = await SnapshotAsync();
            var suspended = await TryWriteAsync(async database =>
            {
                var affected = await database.Database.ExecuteSqlInterpolatedAsync(
                    $"UPDATE identity.users SET status = 'Suspended', updated_at = now() WHERE id = {seed.User.Id}");
                Assert.Equal(1, affected);
            });
            output.WriteLine($"Suspension committed before approval save: {suspended}");
            if (!suspended) Assert.Equal(beforeWriter, await SnapshotAsync());
            var afterWriter = await SnapshotAsync();
            save.Release();
            var result = await approval.WaitAsync(Deadline);
            if (suspended)
            {
                Assert.False(result.IsSuccess);
                Assert.Equal(afterWriter, await SnapshotAsync());
                await AssertRequestAsync(seed, WorkspaceAccessRequestStatus.Pending, UserStatus.Suspended, null);
                await AssertRoleAsync(seed, ["workspace.read"], assignments: 0);
            }
            else
            {
                Assert.True(result.IsSuccess);
                await AssertApprovedAsync(seed, seed.RoleId);
            }
        }
        finally { await DrainAsync(approval); }
    }

    private Task<Result<ApproveWorkspaceAccessRequestResult>> Approve(IServiceScope scope, Seed seed, params object[] adapters)
    {
        var arguments = new object[] { new CurrentUser(seed.Reviewer.Id) }.Concat(adapters).ToArray();
        return ActivatorUtilities.CreateInstance<ApproveWorkspaceAccessRequestCommandHandler>(
            scope.ServiceProvider, arguments).Handle(new(seed.Request.Id));
    }

    private async Task ReachLookupAsync(Task approval)
    {
        var reached = await Task.WhenAny(lookup.Reached, approval).WaitAsync(Deadline);
        Assert.True(reached == lookup.Reached, "Approval completed before the repository compatibility barrier.");
        await lookup.Reached;
    }

    private async Task<bool> ReachSaveAsync(Task approval)
    {
        var reached = await Task.WhenAny(save.Reached, approval).WaitAsync(Deadline);
        return reached == save.Reached;
    }

    private async Task DrainAsync(Task approval)
    {
        lookup.Release();
        save.Release();
        try { await approval.WaitAsync(Deadline); }
        catch when (approval.IsCompleted) { }
    }

    private async Task<bool> TryWriteAsync(Func<ApplicationDbContext, Task> operation)
    {
        await using var database = Host.CreateVerificationContext();
        return await TryWriteContextAsync(database, () => operation(database));
    }

    private static async Task<bool> TryWriteContextAsync(ApplicationDbContext database, Func<Task> operation)
    {
        await using var transaction = await database.Database.BeginTransactionAsync();
        await database.Database.ExecuteSqlRawAsync("SET LOCAL lock_timeout = '1000ms'");
        await database.Database.ExecuteSqlRawAsync("SET LOCAL statement_timeout = '8000ms'");
        try
        {
            await operation();
            await transaction.CommitAsync();
            return true;
        }
        catch (Exception exception) when (exception is PostgresException { SqlState: "55P03" }
            or DbUpdateException { InnerException: PostgresException { SqlState: "55P03" } }
            or InvalidOperationException { InnerException: DbUpdateException { InnerException: PostgresException { SqlState: "55P03" } } })
        {
            await transaction.RollbackAsync();
            return false;
        }
    }

    private async Task<Seed> SeedAsync(bool rolePresent = true, bool readPresent = true, bool assigned = false)
    {
        await using var database = Host.CreateVerificationContext();
        var user = AccessGraph.PendingUser(Guid.NewGuid() + "@example.test");
        var reviewer = AccessGraph.ActiveUser(Guid.NewGuid() + "@example.test");
        var rejectReviewer = AccessGraph.ActiveUser(Guid.NewGuid() + "@example.test");
        var workspace = AccessGraph.NewWorkspace();
        var request = new WorkspaceAccessRequest(Guid.NewGuid(), user.Id, workspace.Id, TestClock.DefaultNow);
        var roleId = Guid.NewGuid();
        var readId = Guid.NewGuid();
        var extraId = Guid.NewGuid();
        var membershipId = Guid.NewGuid();
        database.Users.AddRange(user, reviewer, rejectReviewer);
        database.Workspaces.Add(workspace);
        database.WorkspaceAccessRequests.Add(request);
        database.Permissions.Add(new Permission(extraId, "p008.gap.extra"));
        if (readPresent) database.Permissions.Add(new Permission(readId, "workspace.read"));
        if (rolePresent)
        {
            database.Roles.Add(new Role(roleId, "Member", RoleScope.Workspace, workspace.Id, TestClock.DefaultNow));
            if (readPresent) database.RolePermissions.Add(new(roleId, readId));
        }
        if (assigned)
        {
            database.WorkspaceMemberships.Add(new WorkspaceMembership(membershipId, user.Id, workspace.Id, TestClock.DefaultNow));
            database.WorkspaceMembershipRoles.Add(new(membershipId, roleId));
        }
        await database.SaveChangesAsync();
        return new(user, reviewer, rejectReviewer, workspace, request, roleId, readId, extraId, membershipId);
    }

    private async Task AssertApprovedAsync(Seed seed, Guid? expectedRole)
    {
        await AssertRequestAsync(seed, WorkspaceAccessRequestStatus.Approved, UserStatus.Active, seed.Reviewer.Id);
        await using var database = Host.CreateVerificationContext();
        var membership = Assert.Single(await database.WorkspaceMemberships.ToListAsync());
        Assert.Equal(seed.User.Id, membership.UserId);
        Assert.Equal(seed.Workspace.Id, membership.WorkspaceId);
        var assignment = Assert.Single(await database.WorkspaceMembershipRoles.ToListAsync());
        Assert.Equal(membership.Id, assignment.WorkspaceMembershipId);
        var role = Assert.Single(await database.Roles.ToListAsync());
        Assert.Equal(role.Id, assignment.RoleId);
        if (expectedRole.HasValue) Assert.Equal(expectedRole.Value, role.Id);
        Assert.Equal(seed.Workspace.Id, role.WorkspaceId);
        Assert.Equal(RoleScope.Workspace, role.Scope);
        Assert.Equal("Member", role.Name);
        await AssertRoleAsync(seed with { RoleId = role.Id }, ["workspace.read"], assignments: 1);
    }

    private async Task AssertPendingAsync(Seed seed) =>
        await AssertRequestAsync(seed, WorkspaceAccessRequestStatus.Pending, UserStatus.Pending, null);

    private async Task AssertRequestAsync(Seed seed, WorkspaceAccessRequestStatus status, UserStatus userStatus, Guid? reviewer)
    {
        await using var database = Host.CreateVerificationContext();
        var request = Assert.Single(await database.WorkspaceAccessRequests.ToListAsync());
        Assert.Equal(seed.Request.Id, request.Id);
        Assert.Equal(status, request.Status);
        Assert.Equal(reviewer, request.ReviewedByUserId);
        Assert.Equal(status == WorkspaceAccessRequestStatus.Pending ? (DateTimeOffset?)null : TestClock.DefaultNow, request.ReviewedAt);
        Assert.Equal(userStatus, (await database.Users.SingleAsync(user => user.Id == seed.User.Id)).Status);
        Assert.Empty(await database.UserPlatformRoles.ToListAsync());
    }

    private async Task AssertRoleAsync(Seed seed, string[] codes, int assignments)
    {
        await using var database = Host.CreateVerificationContext();
        var actual = await (from link in database.RolePermissions
                            join permission in database.Permissions on link.PermissionId equals permission.Id
                            where link.RoleId == seed.RoleId
                            orderby permission.Code
                            select permission.Code).ToListAsync();
        Assert.Equal(codes, actual);
        Assert.Equal(assignments, await database.WorkspaceMembershipRoles.CountAsync());
        Assert.Empty(await database.UserPlatformRoles.ToListAsync());
    }

    private async Task<string> SnapshotAsync()
    {
        await using var database = Host.CreateVerificationContext();
        return JsonSerializer.Serialize(new
        {
            Users = await database.Users.OrderBy(row => row.Id).ToListAsync(),
            Requests = await database.WorkspaceAccessRequests.OrderBy(row => row.Id).ToListAsync(),
            Memberships = await database.WorkspaceMemberships.OrderBy(row => row.Id).ToListAsync(),
            Roles = await database.Roles.OrderBy(row => row.Id).ToListAsync(),
            Permissions = await database.Permissions.OrderBy(row => row.Id).ToListAsync(),
            Links = await database.RolePermissions.OrderBy(row => row.RoleId).ThenBy(row => row.PermissionId).ToListAsync(),
            Assignments = await database.WorkspaceMembershipRoles.OrderBy(row => row.WorkspaceMembershipId).ThenBy(row => row.RoleId).ToListAsync(),
            Platform = await database.UserPlatformRoles.OrderBy(row => row.UserId).ThenBy(row => row.RoleId).ToListAsync()
        });
    }

    private sealed record Seed(User User, User Reviewer, User RejectReviewer, Workspace Workspace,
        WorkspaceAccessRequest Request, Guid RoleId, Guid ReadId, Guid ExtraId, Guid MembershipId);
    private sealed class CurrentUser(Guid userId) : ICurrentUser
    {
        public Guid? UserId => userId;
        public bool IsAuthenticated => true;
    }
}
