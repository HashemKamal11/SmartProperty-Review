using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using SmartProperty.Application.Abstractions.Identity;
using SmartProperty.Application.WorkspaceAccessRequests.Approve;
using SmartProperty.Domain.Identity;
using SmartProperty.Domain.Workspaces;
using SmartProperty.Persistence.IntegrationTests.Infrastructure;
using Xunit;

namespace SmartProperty.Persistence.IntegrationTests.Concurrency;

/// <summary>Security specifications: vulnerable baselines must fail, never be blessed as expected failures.</summary>
public sealed class WorkspaceApprovalSecurityTests(PostgreSqlFixture fixture) : DatabaseTest(fixture)
{
    private readonly ApprovalSaveGate gate = new();
    private protected override IInterceptor[] ExtraInterceptors() => [gate];

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MisconfiguredMemberMustFailClosedWithoutRepair(bool extraPermission)
    {
        var seed = await SeedAsync();
        var role = await SeedRoleAsync(seed, assigned: true, read: extraPermission, extra: extraPermission);
        var before = await StateAsync();
        using var scope = Host.CreateScope();
        var result = await Handler(scope, seed.Reviewer.Id).Handle(new(seed.Request.Id));
        var after = await StateAsync();
        Assert.Equal(before, after);
        Assert.False(result.IsSuccess);
        Assert.NotEqual(Guid.Empty, role.Id);
    }

    [Fact]
    public async Task UnassignedEmptyMemberMayInitializeExactlyRead()
    {
        var seed = await SeedAsync();
        var role = await SeedRoleAsync(seed, false, false, false);
        using var scope = Host.CreateScope();
        var result = await Handler(scope, seed.Reviewer.Id).Handle(new(seed.Request.Id));
        Assert.True(result.IsSuccess);
        await AssertApprovedAsync(seed, role.Id);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CompetingApprovalsRecoverRoleOrPermissionCreation(bool differentWorkspaces)
    {
        var first = await SeedAsync();
        var second = await SeedAsync(differentWorkspaces ? null : first.Workspace);
        using var a = Host.CreateScope();
        using var b = Host.CreateScope();
        gate.Arm();
        var pending = Handler(a, first.Reviewer.Id).Handle(new(first.Request.Id));
        await gate.Reached;
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            // Independent scoped connection. A has finished all reads but has not saved.
            var winner = await Handler(b, second.Reviewer.Id).Handle(new(second.Request.Id), timeout.Token);
            Assert.True(winner.IsSuccess);
        }
        finally { gate.Release(); }
        Assert.True((await pending.WaitAsync(TimeSpan.FromSeconds(30))).IsSuccess);
        await AssertApprovedAsync(first);
        await AssertApprovedAsync(second);
        await using var verify = Host.CreateVerificationContext();
        Assert.Equal(1, await verify.Permissions.CountAsync(p => p.Code == "workspace.read"));
        Assert.Equal(differentWorkspaces ? 2 : 1, await verify.Roles.CountAsync());
        Assert.Equal(2, await verify.WorkspaceMembershipRoles.CountAsync());
    }

    [Theory]
    [InlineData("extra")]
    [InlineData("assignment")]
    [InlineData("remove-read")]
    public async Task MutationBeforeApprovalCommitMustBlockOrFailClosed(string mutation)
    {
        var seed = await SeedAsync();
        var role = await SeedRoleAsync(seed, false, mutation != "assignment", false);
        var before = await StateAsync();
        using var scope = Host.CreateScope();
        gate.Arm();
        var approval = Handler(scope, seed.Reviewer.Id).Handle(new(seed.Request.Id));
        await gate.Reached;
        var writerBlocked = false;
        string afterWriter = before;
        try
        {
            await using var writer = Host.CreateVerificationContext();
            await writer.Database.OpenConnectionAsync();
            await writer.Database.ExecuteSqlRawAsync("SET lock_timeout = '1500ms'");
            if (mutation == "assignment")
                writer.WorkspaceMembershipRoles.Add(new(seed.ExistingMembership.Id, role.Id));
            else if (mutation == "extra")
            {
                var extra = AccessGraph.NewPermission("p008.test.extra");
                writer.Permissions.Add(extra);
                writer.RolePermissions.Add(new(role.Id, extra.Id));
            }
            else
                writer.RolePermissions.Remove(await writer.RolePermissions.SingleAsync(p => p.RoleId == role.Id));
            try { await writer.SaveChangesAsync(); }
            catch (Exception exception) when (exception is DbUpdateException { InnerException: PostgresException { SqlState: "55P03" } }
                or InvalidOperationException { InnerException: DbUpdateException { InnerException: PostgresException { SqlState: "55P03" } } })
            { writerBlocked = true; }
            afterWriter = await StateAsync();
        }
        finally { gate.Release(); }
        var result = await approval.WaitAsync(TimeSpan.FromSeconds(30));
        if (writerBlocked)
        {
            Assert.True(result.IsSuccess);
            await AssertApprovedAsync(seed, role.Id);
        }
        else
        {
            // Refusal may not repair another user's authorization or partially activate the applicant.
            Assert.Equal(afterWriter, await StateAsync());
            Assert.False(result.IsSuccess);
        }
    }

    [Fact]
    public async Task FailedForeignKeyRollsBackApprovalAndEveryProvisionedRow()
    {
        var seed = await SeedAsync();
        var before = await StateAsync();
        using var scope = Host.CreateScope();
        // Nonexistent reviewer deliberately reaches the real FK, not a mock SaveChanges failure.
        var error = await Assert.ThrowsAsync<DbUpdateException>(() =>
            Handler(scope, Guid.NewGuid()).Handle(new(seed.Request.Id)));
        Assert.Equal("23503", Assert.IsType<PostgresException>(error.InnerException).SqlState);
        Assert.Equal(before, await StateAsync());
    }

    [Fact]
    public async Task OtherWorkspaceMemberMustNotBeReusedOrRepaired()
    {
        var other = await SeedAsync();
        var bad = await SeedRoleAsync(other, true, false, true);
        var seed = await SeedAsync();
        using var scope = Host.CreateScope();
        Assert.True((await Handler(scope, seed.Reviewer.Id).Handle(new(seed.Request.Id))).IsSuccess);
        await AssertApprovedAsync(seed);
        await using var verify = Host.CreateVerificationContext();
        var codes = await (from rp in verify.RolePermissions join p in verify.Permissions on rp.PermissionId equals p.Id
                           where rp.RoleId == bad.Id select p.Code).ToListAsync();
        Assert.Equal(["p008.test.extra"], codes);
        Assert.Equal(WorkspaceAccessRequestStatus.Pending,
            (await verify.WorkspaceAccessRequests.SingleAsync(r => r.Id == other.Request.Id)).Status);
    }

    private ApproveWorkspaceAccessRequestCommandHandler Handler(IServiceScope scope, Guid reviewer) =>
        ActivatorUtilities.CreateInstance<ApproveWorkspaceAccessRequestCommandHandler>(scope.ServiceProvider, new CurrentUser(reviewer));

    private async Task<Seed> SeedAsync(Workspace? workspace = null)
    {
        await using var db = Host.CreateVerificationContext();
        var user = AccessGraph.PendingUser(Guid.NewGuid() + "@example.test");
        var reviewer = AccessGraph.ActiveUser(Guid.NewGuid() + "@example.test");
        var existing = AccessGraph.ActiveUser(Guid.NewGuid() + "@example.test");
        if (workspace is null) { workspace = AccessGraph.NewWorkspace(); db.Workspaces.Add(workspace); }
        var membership = AccessGraph.NewMembership(existing.Id, workspace.Id);
        var request = new WorkspaceAccessRequest(Guid.NewGuid(), user.Id, workspace.Id, TestClock.DefaultNow);
        db.Users.AddRange(user, reviewer, existing);
        db.WorkspaceMemberships.Add(membership);
        db.WorkspaceAccessRequests.Add(request);
        await db.SaveChangesAsync();
        return new(user, reviewer, workspace, request, membership);
    }

    private async Task<Role> SeedRoleAsync(Seed seed, bool assigned, bool read, bool extra)
    {
        await using var db = Host.CreateVerificationContext();
        var role = AccessGraph.NewWorkspaceRole(seed.Workspace.Id, "Member");
        db.Roles.Add(role);
        foreach (var code in new[] { read ? "workspace.read" : null, extra ? "p008.test.extra" : null }.OfType<string>())
        {
            var permission = AccessGraph.NewPermission(code);
            db.Permissions.Add(permission);
            db.RolePermissions.Add(new(role.Id, permission.Id));
        }
        if (assigned) db.WorkspaceMembershipRoles.Add(new(seed.ExistingMembership.Id, role.Id));
        await db.SaveChangesAsync();
        return role;
    }

    private async Task AssertApprovedAsync(Seed seed, Guid? expectedRole = null)
    {
        await using var db = Host.CreateVerificationContext();
        var request = await db.WorkspaceAccessRequests.SingleAsync(r => r.Id == seed.Request.Id);
        Assert.Equal(WorkspaceAccessRequestStatus.Approved, request.Status);
        Assert.Equal(seed.Reviewer.Id, request.ReviewedByUserId);
        Assert.Equal(UserStatus.Active, (await db.Users.SingleAsync(u => u.Id == seed.User.Id)).Status);
        var membership = await db.WorkspaceMemberships.SingleAsync(m => m.UserId == seed.User.Id && m.WorkspaceId == seed.Workspace.Id);
        var assignment = await db.WorkspaceMembershipRoles.SingleAsync(a => a.WorkspaceMembershipId == membership.Id);
        var role = await db.Roles.SingleAsync(r => r.Id == assignment.RoleId);
        Assert.Equal(seed.Workspace.Id, role.WorkspaceId);
        Assert.Equal(RoleScope.Workspace, role.Scope);
        Assert.Equal("Member", role.Name);
        if (expectedRole.HasValue) Assert.Equal(expectedRole.Value, role.Id);
        var permissions = await (from rp in db.RolePermissions join p in db.Permissions on rp.PermissionId equals p.Id
                                 where rp.RoleId == role.Id select p.Code).ToListAsync();
        Assert.Equal(["workspace.read"], permissions);
        Assert.False(await db.UserPlatformRoles.AnyAsync());
    }

    private async Task<string> StateAsync()
    {
        await using var db = Host.CreateVerificationContext();
        // Full authorization graph, deterministically ordered; synthetic identities only.
        return System.Text.Json.JsonSerializer.Serialize(new {
            Users = await db.Users.OrderBy(x => x.Id).ToListAsync(),
            Requests = await db.WorkspaceAccessRequests.OrderBy(x => x.Id).ToListAsync(),
            Memberships = await db.WorkspaceMemberships.OrderBy(x => x.Id).ToListAsync(),
            Roles = await db.Roles.OrderBy(x => x.Id).ToListAsync(),
            Permissions = await db.Permissions.OrderBy(x => x.Id).ToListAsync(),
            Links = await db.RolePermissions.OrderBy(x => x.RoleId).ThenBy(x => x.PermissionId).ToListAsync(),
            Assignments = await db.WorkspaceMembershipRoles.OrderBy(x => x.WorkspaceMembershipId).ThenBy(x => x.RoleId).ToListAsync(),
            Platform = await db.UserPlatformRoles.OrderBy(x => x.UserId).ThenBy(x => x.RoleId).ToListAsync()
        });
    }

    private sealed record Seed(User User, User Reviewer, Workspace Workspace, WorkspaceAccessRequest Request, WorkspaceMembership ExistingMembership);
    private sealed class CurrentUser(Guid id) : ICurrentUser { public Guid? UserId => id; public bool IsAuthenticated => true; }
}

/// <summary>One-shot rendezvous immediately before an approval save; no sleeps or production seams.</summary>
internal sealed class ApprovalSaveGate : SaveChangesInterceptor
{
    private readonly TaskCompletionSource reached = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource released = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int armed;
    public Task Reached => reached.Task.WaitAsync(TimeSpan.FromSeconds(30));
    public void Arm() => Interlocked.Exchange(ref armed, 1);
    public void Release() => released.TrySetResult();
    public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
        InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        if (Interlocked.CompareExchange(ref armed, 0, 1) == 1)
        {
            reached.TrySetResult();
            await released.Task.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);
        }
        return result;
    }
}
