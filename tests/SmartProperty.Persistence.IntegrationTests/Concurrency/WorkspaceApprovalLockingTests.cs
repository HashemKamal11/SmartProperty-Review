using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using SmartProperty.Application.Abstractions.Identity;
using SmartProperty.Application.Abstractions.Persistence;
using SmartProperty.Application.Abstractions.Persistence.Repositories;
using SmartProperty.Application.WorkspaceAccessRequests.Approve;
using SmartProperty.Common.Results;
using SmartProperty.Domain.Identity;
using SmartProperty.Domain.Workspaces;
using SmartProperty.Persistence.IntegrationTests.Infrastructure;
using Xunit;

namespace SmartProperty.Persistence.IntegrationTests.Concurrency;

/// <summary>
/// Proves against a disposable PostgreSQL database that an approval's decision cannot go stale between its reads and
/// its commit: every other writer that could change what the decision rests on either waits for the approval, or
/// has already committed before the approval reads.
/// </summary>
/// <remarks>
/// Interleavings are controlled by barriers, never by sleeps that decide a result. A pausing coordinator holds an
/// approval after the transaction has begun and every lock is taken, and before the operation has read anything;
/// the test then starts the competing writer and shows that it does not complete while the approval holds its locks.
/// "Does not complete" is observed over a short window and is only ever asserted as still-waiting; what the test
/// then asserts about the outcome does not depend on that window. Writers that must already be in flight when an
/// approval starts hold an open transaction with an uncommitted change. Nothing here touches any database other than
/// the one the fixture created for the test.
/// </remarks>
public sealed class WorkspaceApprovalLockingTests(PostgreSqlFixture fixture) : DatabaseTest(fixture)
{
    private const string MemberRoleName = "Member";
    private const string WorkspaceReadCode = "workspace.read";
    private const string PropertyReadCode = "property.read";

    private static readonly TimeSpan StillWaitingWindow = TimeSpan.FromMilliseconds(1500);
    private static readonly TimeSpan Generous = TimeSpan.FromSeconds(60);

    // ----- an out-of-band role assignment

    [Fact]
    public async Task AnEmptyUnassignedMemberRole_AnAssignmentDuringTheApprovalWaitsAndLandsOnTheCompliantRole()
    {
        var seeded = await SeedPendingAsync();
        var roleId = await SeedMemberRoleAsync(seeded.Workspace.Id);
        await EnsurePermissionAsync(WorkspaceReadCode);
        var holder = await SeedHolderMembershipAsync(seeded.Workspace.Id);

        using var scope = Host.CreateScope();
        var gate = new PausingCoordinator(Real(scope));
        var approval = CreateHandler(scope, seeded.Reviewer.Id, gate)
            .Handle(new ApproveWorkspaceAccessRequestCommand(seeded.Request.Id));
        await gate.WaitUntilLocksAreHeldAsync();

        // The approval holds the role lock and has read nothing yet. An outside writer assigns the empty role.
        var writer = WriteAsync(
            "INSERT INTO identity.workspace_membership_roles (workspace_membership_id, role_id) VALUES (@membership, @role)",
            ("membership", holder), ("role", roleId));
        await AssertStillWaitingAsync(writer);

        gate.Release();
        var result = await approval.WaitAsync(Generous);
        await writer.WaitAsync(Generous);

        // The approval saw an unassigned empty role, completed it, and only then did the assignment land: the
        // outside holder received the role in its approved form, never an empty one the approval then elevated.
        Assert.True(result.IsSuccess);
        Assert.Equal(roleId, await AssertCompliantMemberRoleAsync(seeded.Workspace.Id, expectedHolders: 2));
        await AssertApprovedAsync(seeded);
    }

    [Fact]
    public async Task AnEmptyMemberRole_AnAssignmentAlreadyInFlight_MakesTheApprovalWaitThenFailClosed()
    {
        var seeded = await SeedPendingAsync();
        var roleId = await SeedMemberRoleAsync(seeded.Workspace.Id);
        var holder = await SeedHolderMembershipAsync(seeded.Workspace.Id);

        await using var writer = await BeginWriterAsync();
        await writer.ExecuteAsync(
            "INSERT INTO identity.workspace_membership_roles (workspace_membership_id, role_id) VALUES (@membership, @role)",
            ("membership", holder), ("role", roleId));

        // The assignment is uncommitted. The approval cannot take the role lock until the writer finishes.
        var approval = ApproveAsync(seeded);
        await AssertStillWaitingAsync(approval);

        await writer.CommitAsync();
        var result = await approval.WaitAsync(Generous);

        // After the lock the approval reads the committed assignment, sees an empty role that is held, and refuses.
        AssertMemberRoleMisconfigured(result);
        await AssertNothingApprovedAsync(seeded);

        await using var verification = Host.CreateVerificationContext();
        Assert.False(await verification.RolePermissions.AnyAsync(row => row.RoleId == roleId));
        Assert.False(await verification.Permissions.AnyAsync(row => row.Code == WorkspaceReadCode));
        Assert.Equal(1, await verification.WorkspaceMembershipRoles.CountAsync());
    }

    // ----- out-of-band changes to the role's permissions

    [Fact]
    public async Task ACompliantMemberRole_APermissionInsertedDuringTheApprovalWaitsForIt()
    {
        var seeded = await SeedPendingAsync();
        var roleId = await SeedMemberRoleAsync(seeded.Workspace.Id, WorkspaceReadCode);
        var extraPermissionId = await EnsurePermissionAsync(PropertyReadCode);

        using var scope = Host.CreateScope();
        var gate = new PausingCoordinator(Real(scope));
        var approval = CreateHandler(scope, seeded.Reviewer.Id, gate)
            .Handle(new ApproveWorkspaceAccessRequestCommand(seeded.Request.Id));
        await gate.WaitUntilLocksAreHeldAsync();

        var writer = WriteAsync(
            "INSERT INTO identity.role_permissions (role_id, permission_id) VALUES (@role, @permission)",
            ("role", roleId), ("permission", extraPermissionId));
        await AssertStillWaitingAsync(writer);

        gate.Release();
        var result = await approval.WaitAsync(Generous);
        await writer.WaitAsync(Generous);

        // The approval decided on exactly { workspace.read } and committed on exactly that. The extra permission
        // is a later, separate out-of-band change that the approval neither made nor depended on.
        Assert.True(result.IsSuccess);
        await AssertApprovedAsync(seeded);
    }

    [Fact]
    public async Task ACompliantMemberRole_AnExtraPermissionAlreadyInFlight_MakesTheApprovalWaitThenFailClosed()
    {
        var seeded = await SeedPendingAsync();
        var roleId = await SeedMemberRoleAsync(seeded.Workspace.Id, WorkspaceReadCode);
        var extraPermissionId = await EnsurePermissionAsync(PropertyReadCode);

        await using var writer = await BeginWriterAsync();
        await writer.ExecuteAsync(
            "INSERT INTO identity.role_permissions (role_id, permission_id) VALUES (@role, @permission)",
            ("role", roleId), ("permission", extraPermissionId));

        var approval = ApproveAsync(seeded);
        await AssertStillWaitingAsync(approval);

        await writer.CommitAsync();
        var result = await approval.WaitAsync(Generous);

        AssertMemberRoleMisconfigured(result);
        await AssertNothingApprovedAsync(seeded);

        await using var verification = Host.CreateVerificationContext();
        Assert.Equal(2, await verification.RolePermissions.CountAsync(row => row.RoleId == roleId));
        Assert.False(await verification.WorkspaceMembershipRoles.AnyAsync());
    }

    [Theory]
    [InlineData("update")]
    [InlineData("delete")]
    public async Task ACompliantMemberRole_ChangingOrRemovingItsPermissionLinkDuringTheApprovalWaits(string operation)
    {
        var seeded = await SeedPendingAsync();
        var roleId = await SeedMemberRoleAsync(seeded.Workspace.Id, WorkspaceReadCode);
        var otherPermissionId = await EnsurePermissionAsync(PropertyReadCode);

        using var scope = Host.CreateScope();
        var gate = new PausingCoordinator(Real(scope));
        var approval = CreateHandler(scope, seeded.Reviewer.Id, gate)
            .Handle(new ApproveWorkspaceAccessRequestCommand(seeded.Request.Id));
        await gate.WaitUntilLocksAreHeldAsync();

        // Neither statement inserts a child row, so the role row lock alone would not hold them back; the shared
        // locks on the existing links do.
        var writer = operation == "update"
            ? WriteAsync(
                "UPDATE identity.role_permissions SET permission_id = @permission WHERE role_id = @role",
                ("role", roleId), ("permission", otherPermissionId))
            : WriteAsync("DELETE FROM identity.role_permissions WHERE role_id = @role", ("role", roleId));
        await AssertStillWaitingAsync(writer);

        gate.Release();
        var result = await approval.WaitAsync(Generous);
        await writer.WaitAsync(Generous);

        Assert.True(result.IsSuccess);
        await AssertApprovedAsync(seeded);
    }

    [Fact]
    public async Task ThePermissionRowIsNotRenamedUnderAnApproval()
    {
        var seeded = await SeedPendingAsync();
        await SeedMemberRoleAsync(seeded.Workspace.Id, WorkspaceReadCode);

        using var scope = Host.CreateScope();
        var gate = new PausingCoordinator(Real(scope));
        var approval = CreateHandler(scope, seeded.Reviewer.Id, gate)
            .Handle(new ApproveWorkspaceAccessRequestCommand(seeded.Request.Id));
        await gate.WaitUntilLocksAreHeldAsync();

        var writer = WriteAsync(
            "UPDATE identity.permissions SET code = 'workspace.read.renamed' WHERE code = @code",
            ("code", WorkspaceReadCode));
        await AssertStillWaitingAsync(writer);

        gate.Release();
        var result = await approval.WaitAsync(Generous);
        await writer.WaitAsync(Generous);

        Assert.True(result.IsSuccess);
        await AssertApprovedAsync(seeded);
    }

    // ----- the applicant's account

    [Fact]
    public async Task ASuspensionDuringTheApprovalWaitsAndLandsAfterIt()
    {
        var seeded = await SeedPendingAsync();

        using var scope = Host.CreateScope();
        var gate = new PausingCoordinator(Real(scope));
        var approval = CreateHandler(scope, seeded.Reviewer.Id, gate)
            .Handle(new ApproveWorkspaceAccessRequestCommand(seeded.Request.Id));
        await gate.WaitUntilLocksAreHeldAsync();

        var writer = WriteAsync(
            "UPDATE identity.users SET status = 'Suspended', updated_at = now() WHERE id = @user",
            ("user", seeded.User.Id));
        await AssertStillWaitingAsync(writer);

        gate.Release();
        var result = await approval.WaitAsync(Generous);
        await writer.WaitAsync(Generous);

        // The approval read the applicant under its lock, as it was, and committed on that. The suspension is the
        // later write; an account that is suspended afterwards is refused by every permission check.
        Assert.True(result.IsSuccess);
        await using var verification = Host.CreateVerificationContext();
        Assert.Equal(WorkspaceAccessRequestStatus.Approved, (await verification.WorkspaceAccessRequests.SingleAsync(row => row.Id == seeded.Request.Id)).Status);
        Assert.Equal(UserStatus.Suspended, (await verification.Users.SingleAsync(row => row.Id == seeded.User.Id)).Status);
    }

    [Fact]
    public async Task ASuspensionAlreadyInFlight_MakesTheApprovalWaitThenRefuseTheAccount()
    {
        var seeded = await SeedPendingAsync();

        await using var writer = await BeginWriterAsync();
        await writer.ExecuteAsync(
            "UPDATE identity.users SET status = 'Suspended', updated_at = now() WHERE id = @user",
            ("user", seeded.User.Id));

        var approval = ApproveAsync(seeded);
        await AssertStillWaitingAsync(approval);

        await writer.CommitAsync();
        var result = await approval.WaitAsync(Generous);

        Assert.True(result.IsFailure);
        Assert.Equal("workspace_access_requests.target_account_unavailable", result.Error!.Code);

        await using var verification = Host.CreateVerificationContext();
        Assert.Equal(WorkspaceAccessRequestStatus.Pending, (await verification.WorkspaceAccessRequests.SingleAsync(row => row.Id == seeded.Request.Id)).Status);
        Assert.Equal(UserStatus.Suspended, (await verification.Users.SingleAsync(row => row.Id == seeded.User.Id)).Status);
        Assert.False(await verification.WorkspaceMemberships.AnyAsync());
        Assert.False(await verification.Roles.AnyAsync(row => row.WorkspaceId == seeded.Workspace.Id));
        Assert.False(await verification.Permissions.AnyAsync(row => row.Code == WorkspaceReadCode));
    }

    // ----- other reviewers and other approvals

    [Fact]
    public async Task TwoReviewersOfTheSameRequest_TheSecondWaitsForTheLockAndIsAlreadyReviewed()
    {
        var seeded = await SeedPendingAsync();
        var secondReviewer = await SeedReviewerAsync();

        using var scopeA = Host.CreateScope();
        var gate = new PausingCoordinator(Real(scopeA));
        var approvalA = CreateHandler(scopeA, seeded.Reviewer.Id, gate)
            .Handle(new ApproveWorkspaceAccessRequestCommand(seeded.Request.Id));
        await gate.WaitUntilLocksAreHeldAsync();

        using var scopeB = Host.CreateScope();
        var approvalB = CreateHandler(scopeB, secondReviewer, Real(scopeB))
            .Handle(new ApproveWorkspaceAccessRequestCommand(seeded.Request.Id));
        await AssertStillWaitingAsync(approvalB);

        gate.Release();
        var resultA = await approvalA.WaitAsync(Generous);
        var resultB = await approvalB.WaitAsync(Generous);

        Assert.True(resultA.IsSuccess);
        Assert.True(resultB.IsFailure);
        Assert.Equal("workspace_access_requests.already_reviewed", resultB.Error!.Code);
        await AssertApprovedAsync(seeded);
        await AssertCompliantMemberRoleAsync(seeded.Workspace.Id, expectedHolders: 1);
    }

    [Fact]
    public async Task ApprovalsInTheSameWorkspace_AreSerializedOnTheMemberRoleAndBothSucceed()
    {
        var first = await SeedPendingAsync();
        var second = await SeedPendingApplicantAsync(first.Workspace.Id);
        await SeedMemberRoleAsync(first.Workspace.Id, WorkspaceReadCode);

        using var scopeA = Host.CreateScope();
        var gate = new PausingCoordinator(Real(scopeA));
        var approvalA = CreateHandler(scopeA, first.Reviewer.Id, gate)
            .Handle(new ApproveWorkspaceAccessRequestCommand(first.Request.Id));
        await gate.WaitUntilLocksAreHeldAsync();

        using var scopeB = Host.CreateScope();
        var approvalB = CreateHandler(scopeB, first.Reviewer.Id, Real(scopeB))
            .Handle(new ApproveWorkspaceAccessRequestCommand(second.Request.Id));
        await AssertStillWaitingAsync(approvalB);

        gate.Release();

        Assert.True((await approvalA.WaitAsync(Generous)).IsSuccess);
        Assert.True((await approvalB.WaitAsync(Generous)).IsSuccess);
        await AssertCompliantMemberRoleAsync(first.Workspace.Id, expectedHolders: 2);
    }

    [Fact]
    public async Task AnApprovalInAnotherWorkspace_IsNotHeldBackAndGrantsNothingAcrossWorkspaces()
    {
        var first = await SeedPendingAsync();
        var other = await SeedPendingAsync();

        using var scopeA = Host.CreateScope();
        var gate = new PausingCoordinator(Real(scopeA));
        var approvalA = CreateHandler(scopeA, first.Reviewer.Id, gate)
            .Handle(new ApproveWorkspaceAccessRequestCommand(first.Request.Id));
        await gate.WaitUntilLocksAreHeldAsync();

        // A holds locks only on its own request, applicant, workspace role and links. The other workspace's approval
        // needs none of them.
        using var scopeB = Host.CreateScope();
        var resultB = await CreateHandler(scopeB, other.Reviewer.Id, Real(scopeB))
            .Handle(new ApproveWorkspaceAccessRequestCommand(other.Request.Id))
            .WaitAsync(Generous);
        Assert.True(resultB.IsSuccess);

        gate.Release();
        Assert.True((await approvalA.WaitAsync(Generous)).IsSuccess);

        await AssertCompliantMemberRoleAsync(first.Workspace.Id, expectedHolders: 1);
        await AssertCompliantMemberRoleAsync(other.Workspace.Id, expectedHolders: 1);
        await AssertApprovedAsync(first);
        await AssertApprovedAsync(other);

        await using var verification = Host.CreateVerificationContext();
        Assert.Equal(1, await verification.Permissions.CountAsync(row => row.Code == WorkspaceReadCode));

        // Each applicant holds a role only in their own workspace.
        var firstMembership = await verification.WorkspaceMemberships.SingleAsync(row => row.UserId == first.User.Id);
        var otherMembership = await verification.WorkspaceMemberships.SingleAsync(row => row.UserId == other.User.Id);
        var firstRoles = await verification.Roles
            .Where(role => verification.WorkspaceMembershipRoles.Any(a => a.WorkspaceMembershipId == firstMembership.Id && a.RoleId == role.Id))
            .Select(role => role.WorkspaceId)
            .ToListAsync();
        var otherRoles = await verification.Roles
            .Where(role => verification.WorkspaceMembershipRoles.Any(a => a.WorkspaceMembershipId == otherMembership.Id && a.RoleId == role.Id))
            .Select(role => role.WorkspaceId)
            .ToListAsync();

        Assert.Equal(first.Workspace.Id, Assert.Single(firstRoles));
        Assert.Equal(other.Workspace.Id, Assert.Single(otherRoles));
    }

    [Fact]
    public async Task ARejectWaitsBehindAnApprovalHoldingTheRequestAndLearnsItIsAlreadyReviewed()
    {
        var seeded = await SeedPendingAsync();

        using var scopeA = Host.CreateScope();
        var gate = new PausingCoordinator(Real(scopeA));
        var approval = CreateHandler(scopeA, seeded.Reviewer.Id, gate)
            .Handle(new ApproveWorkspaceAccessRequestCommand(seeded.Request.Id));
        await gate.WaitUntilLocksAreHeldAsync();

        using var rejectingScope = Host.CreateScope();
        var rejection = new SmartProperty.Application.WorkspaceAccessRequests.Reject.RejectWorkspaceAccessRequestCommandHandler(
            new Reviewer(seeded.Reviewer.Id),
            rejectingScope.ServiceProvider.GetRequiredService<IWorkspaceAccessRequestRepository>(),
            rejectingScope.ServiceProvider.GetRequiredService<IUserRepository>(),
            Host.Clock,
            rejectingScope.ServiceProvider.GetRequiredService<IUnitOfWork>())
            .Handle(new SmartProperty.Application.WorkspaceAccessRequests.Reject.RejectWorkspaceAccessRequestCommand(seeded.Request.Id));
        await AssertStillWaitingAsync(rejection);

        gate.Release();
        Assert.True((await approval.WaitAsync(Generous)).IsSuccess);
        var rejectResult = await rejection.WaitAsync(Generous);

        Assert.True(rejectResult.IsFailure);
        Assert.Equal("workspace_access_requests.already_reviewed", rejectResult.Error!.Code);
        await AssertApprovedAsync(seeded);
    }

    // ----- atomic rollback, lock timeout, deadlock

    [Fact]
    public async Task AFailureAfterEveryWriteButBeforeTheCommit_RollsEverythingBack()
    {
        var seeded = await SeedPendingAsync();

        using var scope = Host.CreateScope();
        var failing = new FailAfterOperationCoordinator(Real(scope));
        var handler = CreateHandler(scope, seeded.Reviewer.Id, failing);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => handler.Handle(new ApproveWorkspaceAccessRequestCommand(seeded.Request.Id)));

        // The operation had already saved the decision, activation, membership, role, permission, link and
        // assignment inside the transaction. The rollback removed every one of them.
        Assert.True(failing.OperationCompleted);
        await AssertNothingApprovedAsync(seeded);

        await using var verification = Host.CreateVerificationContext();
        Assert.False(await verification.Roles.AnyAsync(row => row.WorkspaceId == seeded.Workspace.Id));
        Assert.False(await verification.Permissions.AnyAsync(row => row.Code == WorkspaceReadCode));
        Assert.False(await verification.RolePermissions.AnyAsync());
        Assert.False(await verification.WorkspaceMembershipRoles.AnyAsync());
    }

    [Fact]
    public async Task ALockThatCannotBeTaken_TimesOutFailsTheApprovalAndChangesNothing()
    {
        var seeded = await SeedPendingAsync();
        var roleId = await SeedMemberRoleAsync(seeded.Workspace.Id);

        await using var holder = await BeginWriterAsync();
        await holder.ExecuteAsync("SELECT id FROM identity.roles WHERE id = @role FOR UPDATE", ("role", roleId));

        var clock = Stopwatch.StartNew();
        var exception = await Assert.ThrowsAsync<PostgresException>(() => ApproveAsync(seeded));
        clock.Stop();

        // PostgreSQL's lock_timeout, applied by the coordinator, ended the wait. It is neither a hang nor a result.
        Assert.Equal(PostgresErrorCodes.LockNotAvailable, exception.SqlState);
        Assert.InRange(clock.Elapsed, TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(30));

        await holder.RollbackAsync();
        await AssertNothingApprovedAsync(seeded);

        await using var verification = Host.CreateVerificationContext();
        Assert.False(await verification.RolePermissions.AnyAsync());
        Assert.False(await verification.WorkspaceMembershipRoles.AnyAsync());
        Assert.False(await verification.Permissions.AnyAsync(row => row.Code == WorkspaceReadCode));
    }

    [Fact]
    public async Task ADeadlockWithAnUnrelatedWriter_AbortsExactlyOneSideAndLeavesAConsistentDatabase()
    {
        var seeded = await SeedPendingAsync();
        var roleId = await SeedMemberRoleAsync(seeded.Workspace.Id);
        await EnsurePermissionAsync(WorkspaceReadCode);

        // The unrelated writer takes the role lock first and the request lock second: the opposite of the approval.
        await using var writer = await BeginWriterAsync();
        await writer.ExecuteAsync("SELECT id FROM identity.roles WHERE id = @role FOR UPDATE", ("role", roleId));

        var approval = ApproveAsync(seeded);
        await AssertStillWaitingAsync(approval);

        Exception? writerFailure = null;

        try
        {
            await writer.ExecuteAsync(
                "SELECT id FROM identity.workspace_access_requests WHERE id = @id FOR UPDATE",
                ("id", seeded.Request.Id));
            await writer.CommitAsync();
        }
        catch (PostgresException exception)
        {
            writerFailure = exception;
        }

        Result<ApproveWorkspaceAccessRequestResult>? approvalResult = null;
        PostgresException? approvalFailure = null;

        try
        {
            approvalResult = await approval.WaitAsync(Generous);
        }
        catch (PostgresException exception)
        {
            approvalFailure = exception;
        }

        // PostgreSQL chose exactly one victim.
        Assert.True(writerFailure is not null ^ approvalFailure is not null);
        Assert.Equal(PostgresErrorCodes.DeadlockDetected, ((PostgresException?)writerFailure ?? approvalFailure)!.SqlState);

        if (approvalFailure is not null)
        {
            await AssertNothingApprovedAsync(seeded);
        }
        else
        {
            Assert.True(approvalResult!.IsSuccess);
            await AssertApprovedAsync(seeded);
            await AssertCompliantMemberRoleAsync(seeded.Workspace.Id, expectedHolders: 1);
        }
    }

    // ----- rows that become visible after the locks were taken, and assignments the decision rests on
    //
    // Under Read Committed every statement sees what has committed so far. The coordinator locks the rows that exist
    // when it runs; a row committed after that, and before the operation reads, is visible to the operation but was
    // never locked. Each test below commits such a row at the first barrier (locks taken, nothing read), lets the
    // approval decide and write, and holds it at the second barrier (everything written, nothing committed). A competing
    // writer then tries to change what the approval relied on: it must wait for the approval, never complete under it.

    [Fact]
    public async Task ARoleThatBecomesVisibleAfterTheLocks_IsProtectedBeforeItIsRelied_AnExtraPermissionWaits()
    {
        var seeded = await SeedPendingAsync();
        await EnsurePermissionAsync(WorkspaceReadCode);
        var extraPermissionId = await EnsurePermissionAsync(PropertyReadCode);

        using var scope = Host.CreateScope();
        var gate = new BarrierCoordinator(Real(scope));
        var approval = CreateHandler(scope, seeded.Reviewer.Id, gate)
            .Handle(new ApproveWorkspaceAccessRequestCommand(seeded.Request.Id));

        try
        {
            await gate.WaitUntilLocksAreHeldAsync();

            // The Member role did not exist when the coordinator locked; it exists, compliant, by the time the
            // approval reads.
            var roleId = await SeedMemberRoleAsync(seeded.Workspace.Id, WorkspaceReadCode);
            gate.ReleaseOperation();
            await gate.WaitUntilOperationIsDoneAsync(approval);

            var writer = WriteAsync(
                "INSERT INTO identity.role_permissions (role_id, permission_id) VALUES (@role, @permission)",
                ("role", roleId), ("permission", extraPermissionId));
            await AssertStillWaitingAsync(writer);

            gate.ReleaseCommit();
            var result = await approval.WaitAsync(Generous);
            await writer.WaitAsync(Generous);

            Assert.True(result.IsSuccess);
            await AssertApprovedAsync(seeded);
        }
        finally
        {
            gate.ReleaseAll();
        }
    }

    [Theory]
    [InlineData("delete")]
    [InlineData("update")]
    public async Task ARoleLinkThatBecomesVisibleAfterTheLocks_CannotBeChangedOrRemovedUnderTheApproval(string operation)
    {
        var seeded = await SeedPendingAsync();
        await EnsurePermissionAsync(WorkspaceReadCode);
        var otherPermissionId = await EnsurePermissionAsync(PropertyReadCode);

        using var scope = Host.CreateScope();
        var gate = new BarrierCoordinator(Real(scope));
        var approval = CreateHandler(scope, seeded.Reviewer.Id, gate)
            .Handle(new ApproveWorkspaceAccessRequestCommand(seeded.Request.Id));

        try
        {
            await gate.WaitUntilLocksAreHeldAsync();

            var roleId = await SeedMemberRoleAsync(seeded.Workspace.Id, WorkspaceReadCode);
            gate.ReleaseOperation();
            await gate.WaitUntilOperationIsDoneAsync(approval);

            var writer = operation == "delete"
                ? WriteAsync("DELETE FROM identity.role_permissions WHERE role_id = @role", ("role", roleId))
                : WriteAsync(
                    "UPDATE identity.role_permissions SET permission_id = @permission WHERE role_id = @role",
                    ("role", roleId), ("permission", otherPermissionId));
            await AssertStillWaitingAsync(writer);

            gate.ReleaseCommit();
            var result = await approval.WaitAsync(Generous);
            await writer.WaitAsync(Generous);

            Assert.True(result.IsSuccess);
            await AssertApprovedAsync(seeded);
        }
        finally
        {
            gate.ReleaseAll();
        }
    }

    [Fact]
    public async Task AWorkspaceReadPermissionThatBecomesVisibleAfterTheLocks_CannotBeRenamedUnderTheApproval()
    {
        var seeded = await SeedPendingAsync();

        using var scope = Host.CreateScope();
        var gate = new BarrierCoordinator(Real(scope));
        var approval = CreateHandler(scope, seeded.Reviewer.Id, gate)
            .Handle(new ApproveWorkspaceAccessRequestCommand(seeded.Request.Id));

        try
        {
            await gate.WaitUntilLocksAreHeldAsync();

            // Neither the permission nor the role existed when the coordinator locked.
            await SeedMemberRoleAsync(seeded.Workspace.Id, WorkspaceReadCode);
            gate.ReleaseOperation();
            await gate.WaitUntilOperationIsDoneAsync(approval);

            var writer = WriteAsync(
                "UPDATE identity.permissions SET code = @renamed WHERE code = @code",
                ("renamed", "workspace.renamed"), ("code", WorkspaceReadCode));
            await AssertStillWaitingAsync(writer);

            gate.ReleaseCommit();
            var result = await approval.WaitAsync(Generous);
            await writer.WaitAsync(Generous);

            Assert.True(result.IsSuccess);
            await AssertApprovedAsync(seeded);
        }
        finally
        {
            gate.ReleaseAll();
        }
    }

    [Fact]
    public async Task AnExistingAssignmentTheApprovalRelies_CannotBeDeletedUnderIt()
    {
        var seeded = await SeedPendingAsync();
        var roleId = await SeedMemberRoleAsync(seeded.Workspace.Id, WorkspaceReadCode);
        var membershipId = await SeedApplicantMembershipWithRoleAsync(seeded, roleId);

        using var scope = Host.CreateScope();
        var gate = new BarrierCoordinator(Real(scope));
        var approval = CreateHandler(scope, seeded.Reviewer.Id, gate)
            .Handle(new ApproveWorkspaceAccessRequestCommand(seeded.Request.Id));

        try
        {
            await gate.WaitUntilLocksAreHeldAsync();
            gate.ReleaseOperation();
            await gate.WaitUntilOperationIsDoneAsync(approval);

            // The approval found the membership already holding the compliant role and added nothing. Removing that
            // assignment now would commit an approval whose member has no role.
            var writer = WriteAsync(
                "DELETE FROM identity.workspace_membership_roles WHERE workspace_membership_id = @membership AND role_id = @role",
                ("membership", membershipId), ("role", roleId));
            await AssertStillWaitingAsync(writer);

            gate.ReleaseCommit();
            var result = await approval.WaitAsync(Generous);
            await writer.WaitAsync(Generous);

            Assert.True(result.IsSuccess);
            await AssertRequestApprovedAsync(seeded);
        }
        finally
        {
            gate.ReleaseAll();
        }
    }

    [Fact]
    public async Task ARoleAndAssignmentThatBecomeVisibleAfterTheLocks_CannotBeDeletedUnderTheApproval()
    {
        var seeded = await SeedPendingAsync();
        var existingMembershipId = await SeedApplicantMembershipAsync(seeded);

        using var scope = Host.CreateScope();
        var gate = new BarrierCoordinator(Real(scope));
        var approval = CreateHandler(scope, seeded.Reviewer.Id, gate)
            .Handle(new ApproveWorkspaceAccessRequestCommand(seeded.Request.Id));

        try
        {
            await gate.WaitUntilLocksAreHeldAsync();

            // The applicant's membership existed and is locked, but the role did not, so nothing held it back; another
            // path creates the role and assigns it to that membership. (A membership for the applicant cannot appear
            // this way at all: the coordinator holds the applicant's user row, which a new membership must reference.)
            var roleId = await SeedMemberRoleAsync(seeded.Workspace.Id, WorkspaceReadCode);
            await using (var assigning = Host.CreateVerificationContext())
            {
                assigning.WorkspaceMembershipRoles.Add(new WorkspaceMembershipRole(existingMembershipId, roleId));
                await assigning.SaveChangesAsync();
            }

            var membershipId = existingMembershipId;
            gate.ReleaseOperation();
            await gate.WaitUntilOperationIsDoneAsync(approval);

            var writer = WriteAsync(
                "DELETE FROM identity.workspace_membership_roles WHERE workspace_membership_id = @membership AND role_id = @role",
                ("membership", membershipId), ("role", roleId));
            await AssertStillWaitingAsync(writer);

            gate.ReleaseCommit();
            var result = await approval.WaitAsync(Generous);
            await writer.WaitAsync(Generous);

            Assert.True(result.IsSuccess);
            await AssertRequestApprovedAsync(seeded);
        }
        finally
        {
            gate.ReleaseAll();
        }
    }

    // ----- infrastructure

    private static IWorkspaceApprovalCoordinator Real(IServiceScope scope)
    {
        return scope.ServiceProvider.GetRequiredService<IWorkspaceApprovalCoordinator>();
    }

    private ApproveWorkspaceAccessRequestCommandHandler CreateHandler(
        IServiceScope scope,
        Guid reviewerId,
        IWorkspaceApprovalCoordinator coordinator)
    {
        return new ApproveWorkspaceAccessRequestCommandHandler(
            new Reviewer(reviewerId),
            scope.ServiceProvider.GetRequiredService<IWorkspaceAccessRequestRepository>(),
            scope.ServiceProvider.GetRequiredService<IUserRepository>(),
            scope.ServiceProvider.GetRequiredService<IWorkspaceMembershipRepository>(),
            scope.ServiceProvider.GetRequiredService<IRoleRepository>(),
            scope.ServiceProvider.GetRequiredService<IPermissionRepository>(),
            Host.Clock,
            scope.ServiceProvider.GetRequiredService<IUnitOfWork>(),
            coordinator);
    }

    // Starts an approval in its own scope and returns while it may still be waiting. The scope lives until the
    // approval's task completes.
    private Task<Result<ApproveWorkspaceAccessRequestResult>> ApproveAsync(Seeded seeded)
    {
        return Task.Run(async () =>
        {
            using var scope = Host.CreateScope();

            return await CreateHandler(scope, seeded.Reviewer.Id, Real(scope))
                .Handle(new ApproveWorkspaceAccessRequestCommand(seeded.Request.Id));
        });
    }

    private static async Task AssertStillWaitingAsync(Task task)
    {
        var finished = await Task.WhenAny(task, Task.Delay(StillWaitingWindow));

        Assert.NotSame(task, finished);
    }

    private Task WriteAsync(string sql, params (string Name, object Value)[] parameters)
    {
        return Task.Run(async () =>
        {
            await using var connection = new NpgsqlConnection(Host.ConnectionString);
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand(sql, connection);

            foreach (var (name, value) in parameters)
            {
                command.Parameters.AddWithValue(name, value);
            }

            await command.ExecuteNonQueryAsync();
        });
    }

    private async Task<OpenWriter> BeginWriterAsync()
    {
        var connection = new NpgsqlConnection(Host.ConnectionString);
        await connection.OpenAsync();
        var transaction = await connection.BeginTransactionAsync();

        return new OpenWriter(connection, transaction);
    }

    private async Task<Seeded> SeedPendingAsync()
    {
        var user = AccessGraph.PendingUser($"applicant-{Guid.NewGuid():n}@example.test");
        var workspace = AccessGraph.NewWorkspace($"Workspace {Guid.NewGuid():n}");
        var request = new WorkspaceAccessRequest(Guid.NewGuid(), user.Id, workspace.Id, TestClock.DefaultNow);
        var reviewer = AccessGraph.ActiveUser($"reviewer-{Guid.NewGuid():n}@example.test");

        await using var context = Host.CreateVerificationContext();
        context.Users.AddRange(user, reviewer);
        context.Workspaces.Add(workspace);
        context.WorkspaceAccessRequests.Add(request);
        await context.SaveChangesAsync();

        return new Seeded(user, workspace, request, reviewer);
    }

    private async Task<Seeded> SeedPendingApplicantAsync(Guid workspaceId)
    {
        var user = AccessGraph.PendingUser($"applicant-{Guid.NewGuid():n}@example.test");
        var request = new WorkspaceAccessRequest(Guid.NewGuid(), user.Id, workspaceId, TestClock.DefaultNow);

        await using var context = Host.CreateVerificationContext();
        context.Users.Add(user);
        context.WorkspaceAccessRequests.Add(request);
        await context.SaveChangesAsync();

        var workspace = await context.Workspaces.SingleAsync(row => row.Id == workspaceId);

        return new Seeded(user, workspace, request, Reviewer: user);
    }

    private async Task<Guid> SeedReviewerAsync()
    {
        var reviewer = AccessGraph.ActiveUser($"reviewer-{Guid.NewGuid():n}@example.test");

        await using var context = Host.CreateVerificationContext();
        context.Users.Add(reviewer);
        await context.SaveChangesAsync();

        return reviewer.Id;
    }

    private async Task<Guid> SeedHolderMembershipAsync(Guid workspaceId)
    {
        var user = AccessGraph.ActiveUser($"holder-{Guid.NewGuid():n}@example.test");
        var membership = AccessGraph.NewMembership(user.Id, workspaceId);

        await using var context = Host.CreateVerificationContext();
        context.Users.Add(user);
        context.WorkspaceMemberships.Add(membership);
        await context.SaveChangesAsync();

        return membership.Id;
    }

    private async Task<Guid> SeedApplicantMembershipAsync(Seeded seeded)
    {
        var membership = AccessGraph.NewMembership(seeded.User.Id, seeded.Workspace.Id);

        await using var context = Host.CreateVerificationContext();
        context.WorkspaceMemberships.Add(membership);
        await context.SaveChangesAsync();

        return membership.Id;
    }

    private async Task<Guid> SeedApplicantMembershipWithRoleAsync(Seeded seeded, Guid roleId)
    {
        var membershipId = await SeedApplicantMembershipAsync(seeded);

        await using var context = Host.CreateVerificationContext();
        context.WorkspaceMembershipRoles.Add(new WorkspaceMembershipRole(membershipId, roleId));
        await context.SaveChangesAsync();

        return membershipId;
    }

    // The request is approved and the applicant holds a membership; the role assertions of AssertApprovedAsync do not
    // apply because a competing writer may legitimately remove the assignment once the approval has committed.
    private async Task AssertRequestApprovedAsync(Seeded seeded)
    {
        await using var verification = Host.CreateVerificationContext();
        var request = await verification.WorkspaceAccessRequests.SingleAsync(row => row.Id == seeded.Request.Id);

        Assert.Equal(WorkspaceAccessRequestStatus.Approved, request.Status);
        Assert.True(await verification.WorkspaceMemberships.AnyAsync(
            row => row.UserId == seeded.User.Id && row.WorkspaceId == seeded.Workspace.Id));
    }

    private async Task<Guid> EnsurePermissionAsync(string code)
    {
        await using var context = Host.CreateVerificationContext();
        var existing = await context.Permissions.SingleOrDefaultAsync(row => row.Code == code);

        if (existing is not null)
        {
            return existing.Id;
        }

        var permission = AccessGraph.NewPermission(code);
        context.Permissions.Add(permission);
        await context.SaveChangesAsync();

        return permission.Id;
    }

    private async Task<Guid> SeedMemberRoleAsync(Guid workspaceId, params string[] permissionCodes)
    {
        var role = AccessGraph.NewWorkspaceRole(workspaceId, MemberRoleName);

        await using var context = Host.CreateVerificationContext();
        context.Roles.Add(role);
        await context.SaveChangesAsync();

        foreach (var code in permissionCodes)
        {
            var permissionId = await EnsurePermissionAsync(code);
            await using var linking = Host.CreateVerificationContext();
            linking.RolePermissions.Add(new RolePermission(role.Id, permissionId));
            await linking.SaveChangesAsync();
        }

        return role.Id;
    }

    private static void AssertMemberRoleMisconfigured(Result<ApproveWorkspaceAccessRequestResult> result)
    {
        Assert.True(result.IsFailure);
        Assert.Equal("workspace_access_requests.member_role_misconfigured", result.Error!.Code);
        Assert.Equal(ErrorType.Conflict, result.Error.Type);
    }

    private async Task AssertNothingApprovedAsync(Seeded seeded)
    {
        await using var verification = Host.CreateVerificationContext();
        var request = await verification.WorkspaceAccessRequests.SingleAsync(row => row.Id == seeded.Request.Id);

        Assert.Equal(WorkspaceAccessRequestStatus.Pending, request.Status);
        Assert.Null(request.ReviewedByUserId);
        Assert.Null(request.ReviewedAt);
        Assert.False(await verification.WorkspaceMemberships.AnyAsync(row => row.UserId == seeded.User.Id));

        if (seeded.User.Id != seeded.Reviewer.Id)
        {
            Assert.Equal(UserStatus.Pending, (await verification.Users.SingleAsync(row => row.Id == seeded.User.Id)).Status);
        }
    }

    private async Task AssertApprovedAsync(Seeded seeded)
    {
        await using var verification = Host.CreateVerificationContext();
        var request = await verification.WorkspaceAccessRequests.SingleAsync(row => row.Id == seeded.Request.Id);

        Assert.Equal(WorkspaceAccessRequestStatus.Approved, request.Status);

        var membership = await verification.WorkspaceMemberships.SingleAsync(
            row => row.UserId == seeded.User.Id && row.WorkspaceId == seeded.Workspace.Id);
        var memberRole = await verification.Roles.SingleAsync(
            row => row.WorkspaceId == seeded.Workspace.Id && row.Scope == RoleScope.Workspace && row.Name == MemberRoleName);

        Assert.Equal(1, await verification.WorkspaceMembershipRoles.CountAsync(
            row => row.WorkspaceMembershipId == membership.Id && row.RoleId == memberRole.Id));
    }

    private async Task<Guid> AssertCompliantMemberRoleAsync(Guid workspaceId, int expectedHolders)
    {
        await using var verification = Host.CreateVerificationContext();
        var memberRole = await verification.Roles.SingleAsync(
            row => row.WorkspaceId == workspaceId && row.Scope == RoleScope.Workspace && row.Name == MemberRoleName);

        var permissionIds = await verification.RolePermissions
            .Where(row => row.RoleId == memberRole.Id)
            .Select(row => row.PermissionId)
            .ToListAsync();
        var onlyPermissionId = Assert.Single(permissionIds);

        Assert.Equal(WorkspaceReadCode, (await verification.Permissions.SingleAsync(row => row.Id == onlyPermissionId)).Code);
        Assert.Equal(
            expectedHolders,
            await verification.WorkspaceMembershipRoles.CountAsync(row => row.RoleId == memberRole.Id));

        return memberRole.Id;
    }

    private sealed record Seeded(User User, Workspace Workspace, WorkspaceAccessRequest Request, User Reviewer);

    private sealed class Reviewer(Guid userId) : ICurrentUser
    {
        public Guid? UserId { get; } = userId;

        public bool IsAuthenticated => true;
    }

    /// <summary>An open connection and transaction with an uncommitted change, standing in for an unrelated writer.</summary>
    private sealed class OpenWriter(NpgsqlConnection connection, NpgsqlTransaction transaction) : IAsyncDisposable
    {
        public async Task ExecuteAsync(string sql, params (string Name, object Value)[] parameters)
        {
            await using var command = new NpgsqlCommand(sql, connection, transaction);

            foreach (var (name, value) in parameters)
            {
                command.Parameters.AddWithValue(name, value);
            }

            await using var reader = await command.ExecuteReaderAsync();

            while (await reader.ReadAsync())
            {
            }
        }

        public Task CommitAsync() => transaction.CommitAsync();

        public Task RollbackAsync() => transaction.RollbackAsync();

        public async ValueTask DisposeAsync()
        {
            await transaction.DisposeAsync();
            await connection.DisposeAsync();
        }
    }

    /// <summary>
    /// Runs the real coordinator, but pauses the operation after the transaction has begun and every lock is held,
    /// before the operation reads anything.
    /// </summary>
    private sealed class PausingCoordinator(IWorkspaceApprovalCoordinator inner) : IWorkspaceApprovalCoordinator
    {
        private readonly TaskCompletionSource locksHeld = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource released = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task WaitUntilLocksAreHeldAsync() => locksHeld.Task.WaitAsync(Generous);

        public void Release() => released.TrySetResult();

        public Task ConfirmLockedAsync(
            Guid? membershipId, Guid? roleId, Guid? permissionId, CancellationToken cancellationToken = default) =>
            inner.ConfirmLockedAsync(membershipId, roleId, permissionId, cancellationToken);

        public Task<T> ExecuteAsync<T>(
            Guid accessRequestId,
            Func<CancellationToken, Task<T>> operation,
            CancellationToken cancellationToken = default)
        {
            return inner.ExecuteAsync(
                accessRequestId,
                async token =>
                {
                    locksHeld.TrySetResult();
                    await released.Task.WaitAsync(token);

                    return await operation(token);
                },
                cancellationToken);
        }
    }

    /// <summary>
    /// Runs the real coordinator and holds the approval at two barriers: after the transaction has begun and every lock
    /// is held, before the operation reads anything; and after the operation has read and written everything, before the
    /// coordinator commits. A retry of the operation passes both barriers once they have been released.
    /// </summary>
    private sealed class BarrierCoordinator(IWorkspaceApprovalCoordinator inner) : IWorkspaceApprovalCoordinator
    {
        private readonly TaskCompletionSource locksHeld = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource operationReleased = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource operationDone = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource commitReleased = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task WaitUntilLocksAreHeldAsync() => locksHeld.Task.WaitAsync(Generous);

        public void ReleaseOperation() => operationReleased.TrySetResult();

        // Returns when the operation has finished its reads and writes, or when the approval has ended without getting
        // there, in which case the approval's own failure is surfaced rather than a timeout.
        public async Task WaitUntilOperationIsDoneAsync(Task approval)
        {
            var finished = await Task.WhenAny(operationDone.Task, approval).WaitAsync(Generous);

            if (finished == approval)
            {
                await approval;

                throw new InvalidOperationException("The approval ended before it reached the commit barrier.");
            }
        }

        public void ReleaseCommit() => commitReleased.TrySetResult();

        public Task ConfirmLockedAsync(
            Guid? membershipId, Guid? roleId, Guid? permissionId, CancellationToken cancellationToken = default) =>
            inner.ConfirmLockedAsync(membershipId, roleId, permissionId, cancellationToken);

        public void ReleaseAll()
        {
            operationReleased.TrySetResult();
            commitReleased.TrySetResult();
        }

        public Task<T> ExecuteAsync<T>(
            Guid accessRequestId,
            Func<CancellationToken, Task<T>> operation,
            CancellationToken cancellationToken = default)
        {
            return inner.ExecuteAsync(
                accessRequestId,
                async token =>
                {
                    locksHeld.TrySetResult();
                    await operationReleased.Task.WaitAsync(token);

                    var result = await operation(token);

                    operationDone.TrySetResult();
                    await commitReleased.Task.WaitAsync(token);

                    return result;
                },
                cancellationToken);
        }
    }

    /// <summary>Runs the real operation to completion, then fails before the coordinator can commit it.</summary>
    private sealed class FailAfterOperationCoordinator(IWorkspaceApprovalCoordinator inner) : IWorkspaceApprovalCoordinator
    {
        public bool OperationCompleted { get; private set; }

        public Task ConfirmLockedAsync(
            Guid? membershipId, Guid? roleId, Guid? permissionId, CancellationToken cancellationToken = default) =>
            inner.ConfirmLockedAsync(membershipId, roleId, permissionId, cancellationToken);

        public Task<T> ExecuteAsync<T>(
            Guid accessRequestId,
            Func<CancellationToken, Task<T>> operation,
            CancellationToken cancellationToken = default)
        {
            return inner.ExecuteAsync<T>(
                accessRequestId,
                async token =>
                {
                    await operation(token);
                    OperationCompleted = true;

                    throw new InvalidOperationException("Simulated failure after the writes and before the commit.");
                },
                cancellationToken);
        }
    }
}
