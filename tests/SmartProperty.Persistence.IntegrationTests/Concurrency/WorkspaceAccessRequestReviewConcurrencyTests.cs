using System.Collections.Concurrent;
using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using SmartProperty.Application.Abstractions.Identity;
using SmartProperty.Application.Abstractions.Persistence;
using SmartProperty.Application.Abstractions.Persistence.Repositories;
using SmartProperty.Application.WorkspaceAccessRequests.Approve;
using SmartProperty.Application.WorkspaceAccessRequests.Reject;
using SmartProperty.Common.Results;
using SmartProperty.Domain.Identity;
using SmartProperty.Domain.Workspaces;
using SmartProperty.Persistence.Context;
using SmartProperty.Persistence.IntegrationTests.Infrastructure;
using Xunit;

namespace SmartProperty.Persistence.IntegrationTests.Concurrency;

/// <summary>
/// Proves against PostgreSQL that two independent request scopes cannot both commit a terminal review of the
/// same access request after both loaded its original Pending state.
/// </summary>
public sealed class WorkspaceAccessRequestReviewConcurrencyTests(PostgreSqlFixture fixture) : DatabaseTest(fixture)
{
    private const string MemberRoleName = "Member";
    private const string WorkspaceReadCode = "workspace.read";
    private const string PropertyReadCode = "property.read";
    private const string PlatformReviewCode = "workspace.access_requests.review";

    private readonly WorkspaceMembershipLookupBarrier membershipLookupBarrier = new();
    private readonly ControlledSaveInterceptor saves = new();
    private readonly SecondPolicyReadPause secondPolicyRead = new();

    private protected override IInterceptor[] ExtraInterceptors()
    {
        return [membershipLookupBarrier, saves, secondPolicyRead];
    }

    [Fact]
    public async Task ApproveWinsAgainstStaleReject_AndAllApprovalStateCommitsTogether()
    {
        var seeded = await SeedPendingRequestAsync();
        using var approvingScope = Host.CreateScope();
        using var rejectingScope = Host.CreateScope();

        var approvingRequest = await GetRequestAsync(approvingScope, seeded.Request.Id);
        var approvingUser = await GetUserAsync(approvingScope, seeded.User.Id);
        var rejectingRequest = await GetRequestAsync(rejectingScope, seeded.Request.Id);
        _ = await GetUserAsync(rejectingScope, seeded.User.Id);

        // Both independent contexts have observed Pending before either context changes or saves it.
        Assert.Equal(WorkspaceAccessRequestStatus.Pending, approvingRequest.Status);
        Assert.Equal(WorkspaceAccessRequestStatus.Pending, rejectingRequest.Status);

        var approvedAt = TestClock.DefaultNow.AddMinutes(1);
        var rejectedAt = TestClock.DefaultNow.AddMinutes(2);
        var membership = new WorkspaceMembership(Guid.NewGuid(), seeded.User.Id, seeded.Workspace.Id, approvedAt);

        approvingRequest.Approve(seeded.ReviewerA.Id, approvedAt);
        approvingUser.Activate(approvedAt);
        await approvingScope.ServiceProvider
            .GetRequiredService<IWorkspaceMembershipRepository>()
            .AddAsync(membership);
        rejectingRequest.Reject(seeded.ReviewerB.Id, rejectedAt);

        await SaveAsync(approvingScope);
        var conflict = await Assert.ThrowsAsync<ConcurrencyConflictException>(() => SaveAsync(rejectingScope));

        Assert.Equal(PersistenceResource.WorkspaceAccessRequest, conflict.Resource);

        await using var verification = Host.CreateVerificationContext();
        var persistedRequest = await verification.WorkspaceAccessRequests.SingleAsync(row => row.Id == seeded.Request.Id);
        var persistedUser = await verification.Users.SingleAsync(row => row.Id == seeded.User.Id);
        var memberships = await verification.WorkspaceMemberships
            .Where(row => row.UserId == seeded.User.Id && row.WorkspaceId == seeded.Workspace.Id)
            .ToListAsync();

        Assert.Equal(WorkspaceAccessRequestStatus.Approved, persistedRequest.Status);
        Assert.Equal(seeded.ReviewerA.Id, persistedRequest.ReviewedByUserId);
        Assert.Equal(approvedAt, persistedRequest.ReviewedAt);
        Assert.Equal(UserStatus.Active, persistedUser.Status);
        Assert.Equal(membership.Id, Assert.Single(memberships).Id);
    }

    [Fact]
    public async Task RejectWinsAgainstStaleApprove_AndApprovalSideEffectsRollBack()
    {
        var seeded = await SeedPendingRequestAsync();
        using var rejectingScope = Host.CreateScope();
        using var approvingScope = Host.CreateScope();

        var rejectingRequest = await GetRequestAsync(rejectingScope, seeded.Request.Id);
        _ = await GetUserAsync(rejectingScope, seeded.User.Id);
        var approvingRequest = await GetRequestAsync(approvingScope, seeded.Request.Id);
        var approvingUser = await GetUserAsync(approvingScope, seeded.User.Id);

        Assert.Equal(WorkspaceAccessRequestStatus.Pending, rejectingRequest.Status);
        Assert.Equal(WorkspaceAccessRequestStatus.Pending, approvingRequest.Status);

        var rejectedAt = TestClock.DefaultNow.AddMinutes(1);
        var approvedAt = TestClock.DefaultNow.AddMinutes(2);

        rejectingRequest.Reject(seeded.ReviewerA.Id, rejectedAt);
        approvingRequest.Approve(seeded.ReviewerB.Id, approvedAt);
        approvingUser.Activate(approvedAt);
        await approvingScope.ServiceProvider
            .GetRequiredService<IWorkspaceMembershipRepository>()
            .AddAsync(new WorkspaceMembership(Guid.NewGuid(), seeded.User.Id, seeded.Workspace.Id, approvedAt));

        await SaveAsync(rejectingScope);
        var conflict = await Assert.ThrowsAsync<ConcurrencyConflictException>(() => SaveAsync(approvingScope));

        Assert.Equal(PersistenceResource.WorkspaceAccessRequest, conflict.Resource);

        await using var verification = Host.CreateVerificationContext();
        var persistedRequest = await verification.WorkspaceAccessRequests.SingleAsync(row => row.Id == seeded.Request.Id);
        var persistedUser = await verification.Users.SingleAsync(row => row.Id == seeded.User.Id);

        Assert.Equal(WorkspaceAccessRequestStatus.Rejected, persistedRequest.Status);
        Assert.Equal(seeded.ReviewerA.Id, persistedRequest.ReviewedByUserId);
        Assert.Equal(rejectedAt, persistedRequest.ReviewedAt);
        Assert.Equal(UserStatus.Pending, persistedUser.Status);
        Assert.False(await verification.WorkspaceMemberships
            .AnyAsync(row => row.UserId == seeded.User.Id && row.WorkspaceId == seeded.Workspace.Id));
    }

    [Fact]
    public async Task FirstApproveWinsAgainstStaleApprove_AndExactlyOneMembershipExists()
    {
        var seeded = await SeedPendingRequestAsync();
        using var winnerScope = Host.CreateScope();
        using var loserScope = Host.CreateScope();

        var winningRequest = await GetRequestAsync(winnerScope, seeded.Request.Id);
        var winningUser = await GetUserAsync(winnerScope, seeded.User.Id);
        var losingRequest = await GetRequestAsync(loserScope, seeded.Request.Id);
        var losingUser = await GetUserAsync(loserScope, seeded.User.Id);

        Assert.Equal(WorkspaceAccessRequestStatus.Pending, winningRequest.Status);
        Assert.Equal(WorkspaceAccessRequestStatus.Pending, losingRequest.Status);

        var winningAt = TestClock.DefaultNow.AddMinutes(1);
        var losingAt = TestClock.DefaultNow.AddMinutes(2);
        var winningMembership = new WorkspaceMembership(
            Guid.NewGuid(), seeded.User.Id, seeded.Workspace.Id, winningAt);

        winningRequest.Approve(seeded.ReviewerA.Id, winningAt);
        winningUser.Activate(winningAt);
        await winnerScope.ServiceProvider.GetRequiredService<IWorkspaceMembershipRepository>()
            .AddAsync(winningMembership);

        losingRequest.Approve(seeded.ReviewerB.Id, losingAt);
        losingUser.Activate(losingAt);
        await loserScope.ServiceProvider.GetRequiredService<IWorkspaceMembershipRepository>()
            .AddAsync(new WorkspaceMembership(Guid.NewGuid(), seeded.User.Id, seeded.Workspace.Id, losingAt));

        await SaveAsync(winnerScope);
        var losingFailure = await Record.ExceptionAsync(() => SaveAsync(loserScope));

        AssertControlledApprovalConflict(losingFailure);

        await using var verification = Host.CreateVerificationContext();
        var persistedRequest = await verification.WorkspaceAccessRequests.SingleAsync(row => row.Id == seeded.Request.Id);
        var persistedUser = await verification.Users.SingleAsync(row => row.Id == seeded.User.Id);
        var memberships = await verification.WorkspaceMemberships
            .Where(row => row.UserId == seeded.User.Id && row.WorkspaceId == seeded.Workspace.Id)
            .ToListAsync();

        Assert.Equal(WorkspaceAccessRequestStatus.Approved, persistedRequest.Status);
        Assert.Equal(seeded.ReviewerA.Id, persistedRequest.ReviewedByUserId);
        Assert.Equal(winningAt, persistedRequest.ReviewedAt);
        Assert.Equal(UserStatus.Active, persistedUser.Status);
        Assert.Equal(winningMembership.Id, Assert.Single(memberships).Id);
    }

    [Fact]
    public async Task FirstRejectWinsAgainstStaleReject_AndWinnerMetadataIsPreserved()
    {
        var seeded = await SeedPendingRequestAsync();
        using var winnerScope = Host.CreateScope();
        using var loserScope = Host.CreateScope();

        var winningRequest = await GetRequestAsync(winnerScope, seeded.Request.Id);
        _ = await GetUserAsync(winnerScope, seeded.User.Id);
        var losingRequest = await GetRequestAsync(loserScope, seeded.Request.Id);
        _ = await GetUserAsync(loserScope, seeded.User.Id);

        Assert.Equal(WorkspaceAccessRequestStatus.Pending, winningRequest.Status);
        Assert.Equal(WorkspaceAccessRequestStatus.Pending, losingRequest.Status);

        var winningAt = TestClock.DefaultNow.AddMinutes(1);
        winningRequest.Reject(seeded.ReviewerA.Id, winningAt);
        losingRequest.Reject(seeded.ReviewerB.Id, TestClock.DefaultNow.AddMinutes(2));

        await SaveAsync(winnerScope);
        var conflict = await Assert.ThrowsAsync<ConcurrencyConflictException>(() => SaveAsync(loserScope));

        Assert.Equal(PersistenceResource.WorkspaceAccessRequest, conflict.Resource);

        await using var verification = Host.CreateVerificationContext();
        var persistedRequest = await verification.WorkspaceAccessRequests.SingleAsync(row => row.Id == seeded.Request.Id);

        Assert.Equal(WorkspaceAccessRequestStatus.Rejected, persistedRequest.Status);
        Assert.Equal(seeded.ReviewerA.Id, persistedRequest.ReviewedByUserId);
        Assert.Equal(winningAt, persistedRequest.ReviewedAt);
        Assert.False(await verification.WorkspaceMemberships
            .AnyAsync(row => row.UserId == seeded.User.Id && row.WorkspaceId == seeded.Workspace.Id));
    }

    // ---- P-008: Member role policy and its concurrency recovery, through the real handler and real PostgreSQL ----
    //
    // Interleavings are controlled by ControlledSaveInterceptor, never by a delay. A handler that has reached
    // SavingChanges has already read, decided (including the Member-role plan) and mutated, so holding it there
    // fixes exactly what it believes before another writer commits. Releasing it makes the real SaveChanges run
    // against real PostgreSQL, where the unique indexes, composite keys and row locks decide the outcome. Each
    // test then asserts the exact constraint PostgreSQL reported, not merely that the approvals ended well.

    [Fact]
    public async Task ConcurrentApprovalsCreatingTheSameMemberRole_LeaveOneCompliantRoleAndOneWorkspaceReadLink()
    {
        ReleaseMembershipLookupBarrier();
        var seeded = await SeedPendingRequestAsync();
        var second = await SeedPendingApplicantAsync(seeded.Workspace.Id);
        using var scopeA = Host.CreateScope();
        using var scopeB = Host.CreateScope();

        // Neither Member role nor workspace.read exists, so both approvals plan to create them.
        saves.ArmBarrier(ContextOf(scopeA), ContextOf(scopeB));

        var approvalA = CreateApproveHandler(scopeA, seeded.ReviewerA.Id)
            .Handle(new ApproveWorkspaceAccessRequestCommand(seeded.Request.Id));
        var approvalB = CreateApproveHandler(scopeB, seeded.ReviewerB.Id)
            .Handle(new ApproveWorkspaceAccessRequestCommand(second.Request.Id));
        var results = await WhenAllOrReleaseAsync(approvalA, approvalB);

        // One commits; the other hits a unique index, discards, reloads, and reuses what the winner wrote.
        Assert.All(results, result => Assert.True(result.IsSuccess));

        // PostgreSQL rejected exactly one save, on whichever of the two identical inserts it reached first.
        var failure = Assert.Single(saves.Failures);
        Assert.Contains(failure, new[] { "ux_identity_roles_workspace_name", "ux_identity_permissions_code" });

        await AssertCompliantMemberRoleAsync(seeded.Workspace.Id, expectedHolders: 2);
        await AssertApprovedAsync(seeded.Request.Id, seeded.User.Id, seeded.Workspace.Id);
        await AssertApprovedAsync(second.Request.Id, second.User.Id, seeded.Workspace.Id);
    }

    [Fact]
    public async Task ConcurrentFirstApprovalsInTwoWorkspaces_CreateWorkspaceReadOnceAndGrantNothingAcrossWorkspaces()
    {
        ReleaseMembershipLookupBarrier();
        var first = await SeedPendingRequestAsync();
        var second = await SeedPendingRequestAsync();
        using var scopeA = Host.CreateScope();
        using var scopeB = Host.CreateScope();

        // Different workspaces share no request, applicant, role or link, so no row lock holds either back: both reach
        // the save having planned to create the one global workspace.read permission.
        saves.ArmBarrier(ContextOf(scopeA), ContextOf(scopeB));

        var approvalA = CreateApproveHandler(scopeA, first.ReviewerA.Id)
            .Handle(new ApproveWorkspaceAccessRequestCommand(first.Request.Id));
        var approvalB = CreateApproveHandler(scopeB, second.ReviewerA.Id)
            .Handle(new ApproveWorkspaceAccessRequestCommand(second.Request.Id));
        var results = await WhenAllOrReleaseAsync(approvalA, approvalB);

        Assert.All(results, result => Assert.True(result.IsSuccess));

        // PostgreSQL rejected exactly one save, on the permission code, and the loser recovered in a new transaction.
        Assert.Equal("ux_identity_permissions_code", Assert.Single(saves.Failures));

        await AssertCompliantMemberRoleAsync(first.Workspace.Id, expectedHolders: 1);
        await AssertCompliantMemberRoleAsync(second.Workspace.Id, expectedHolders: 1);
        await AssertApprovedAsync(first.Request.Id, first.User.Id, first.Workspace.Id);
        await AssertApprovedAsync(second.Request.Id, second.User.Id, second.Workspace.Id);

        await using var verification = Host.CreateVerificationContext();
        Assert.Equal(1, await verification.Permissions.CountAsync(row => row.Code == WorkspaceReadCode));
        Assert.Equal(2, await verification.Roles.CountAsync(row => row.Name == MemberRoleName));
        Assert.Equal(2, await verification.WorkspaceMembershipRoles.CountAsync());
    }

    [Fact]
    public async Task ConcurrentApprovalsInitializingAnEmptyUnassignedMemberRole_AttachWorkspaceReadExactlyOnce()
    {
        ReleaseMembershipLookupBarrier();
        var seeded = await SeedPendingRequestAsync();
        var second = await SeedPendingApplicantAsync(seeded.Workspace.Id);
        var memberRoleId = await SeedMemberRoleAsync(seeded.Workspace.Id);
        await EnsurePermissionAsync(WorkspaceReadCode);
        using var scopeA = Host.CreateScope();
        using var scopeB = Host.CreateScope();

        // Both would complete the same empty role. The role row lock makes the second approval wait until the first
        // has committed, so it reads a compliant, assigned role and reuses it: no save is ever rejected.

        var approvalA = CreateApproveHandler(scopeA, seeded.ReviewerA.Id)
            .Handle(new ApproveWorkspaceAccessRequestCommand(seeded.Request.Id));
        var approvalB = CreateApproveHandler(scopeB, seeded.ReviewerB.Id)
            .Handle(new ApproveWorkspaceAccessRequestCommand(second.Request.Id));
        var results = await Task.WhenAll(approvalA, approvalB);

        Assert.All(results, result => Assert.True(result.IsSuccess));

        // Neither approval needed recovery.
        Assert.Empty(saves.Failures);

        // The pre-existing role was initialized and reused, never duplicated.
        Assert.Equal(memberRoleId, await AssertCompliantMemberRoleAsync(seeded.Workspace.Id, expectedHolders: 2));
        await AssertApprovedAsync(seeded.Request.Id, seeded.User.Id, seeded.Workspace.Id);
        await AssertApprovedAsync(second.Request.Id, second.User.Id, seeded.Workspace.Id);
    }

    [Fact]
    public async Task ConcurrentApprovalsOfTheSameRequest_OneWinsAndTheOtherIsAlreadyReviewed()
    {
        ReleaseMembershipLookupBarrier();
        var seeded = await SeedPendingRequestAsync();
        using var scopeA = Host.CreateScope();
        using var scopeB = Host.CreateScope();

        var approvalA = CreateApproveHandler(scopeA, seeded.ReviewerA.Id)
            .Handle(new ApproveWorkspaceAccessRequestCommand(seeded.Request.Id));
        var approvalB = CreateApproveHandler(scopeB, seeded.ReviewerB.Id)
            .Handle(new ApproveWorkspaceAccessRequestCommand(seeded.Request.Id));
        var results = await Task.WhenAll(approvalA, approvalB);

        var winners = results.Where(result => result.IsSuccess).ToList();
        var losers = results.Where(result => result.IsFailure).ToList();

        // The second reviewer waits on the request row lock until the first has committed, then reads the request as
        // already reviewed. The only answer it may give is "already reviewed", and no save of its own is attempted.
        Assert.Single(winners);
        var loser = Assert.Single(losers);
        Assert.Equal("workspace_access_requests.already_reviewed", loser.Error!.Code);

        Assert.Empty(saves.Failures);

        await AssertCompliantMemberRoleAsync(seeded.Workspace.Id, expectedHolders: 1);
        await AssertApprovedAsync(seeded.Request.Id, seeded.User.Id, seeded.Workspace.Id);

        await using var verification = Host.CreateVerificationContext();
        var persistedRequest = await verification.WorkspaceAccessRequests.SingleAsync(
            row => row.Id == seeded.Request.Id);
        Assert.Contains(persistedRequest.ReviewedByUserId, new Guid?[] { seeded.ReviewerA.Id, seeded.ReviewerB.Id });
    }

    [Theory]
    [InlineData(true, PropertyReadCode)]
    [InlineData(true, PlatformReviewCode)]
    [InlineData(false, PropertyReadCode)]
    [InlineData(false, PlatformReviewCode)]
    public async Task ExistingMemberRoleWithAnyOtherPermission_FailsClosedAndChangesNothing(
        bool alsoCarriesWorkspaceRead,
        string otherPermissionCode)
    {
        ReleaseMembershipLookupBarrier();
        var seeded = await SeedPendingRequestAsync();
        var codes = alsoCarriesWorkspaceRead
            ? new[] { WorkspaceReadCode, otherPermissionCode }
            : new[] { otherPermissionCode };
        var memberRoleId = await SeedMemberRoleAsync(seeded.Workspace.Id, codes);

        var result = await ApproveAsync(seeded.Request.Id, seeded.ReviewerA.Id);

        AssertMemberRoleMisconfigured(result);
        await AssertNothingApprovedAsync(seeded);

        // The role is reported, never repaired.
        await using var verification = Host.CreateVerificationContext();
        Assert.Equal(codes.Length, await verification.RolePermissions.CountAsync(row => row.RoleId == memberRoleId));
        Assert.False(await verification.WorkspaceMembershipRoles.AnyAsync());
    }

    [Fact]
    public async Task ExistingEmptyMemberRoleThatIsAlreadyAssigned_FailsClosedAndChangesNothing()
    {
        ReleaseMembershipLookupBarrier();
        var seeded = await SeedPendingRequestAsync();
        var memberRoleId = await SeedMemberRoleAsync(seeded.Workspace.Id);
        var otherUser = AccessGraph.ActiveUser($"holder-{Guid.NewGuid():n}@example.test");
        var holder = AccessGraph.NewMembership(otherUser.Id, seeded.Workspace.Id);

        await using (var seeding = Host.CreateVerificationContext())
        {
            seeding.Users.Add(otherUser);
            seeding.WorkspaceMemberships.Add(holder);
            seeding.WorkspaceMembershipRoles.Add(new WorkspaceMembershipRole(holder.Id, memberRoleId));
            await seeding.SaveChangesAsync();
        }

        var result = await ApproveAsync(seeded.Request.Id, seeded.ReviewerA.Id);

        AssertMemberRoleMisconfigured(result);
        await AssertNothingApprovedAsync(seeded);

        await using var verification = Host.CreateVerificationContext();
        Assert.False(await verification.RolePermissions.AnyAsync(row => row.RoleId == memberRoleId));
        Assert.Equal(1, await verification.WorkspaceMembershipRoles.CountAsync());
        Assert.False(await verification.Permissions.AnyAsync(row => row.Code == WorkspaceReadCode));
    }

    [Fact]
    public async Task OtherWorkspaceRolesAndPlatformPermissions_RemainIsolatedFromAnApproval()
    {
        ReleaseMembershipLookupBarrier();
        var seeded = await SeedPendingRequestAsync();

        // A misconfigured, assigned Member role in ANOTHER workspace must neither block nor be touched.
        var otherWorkspace = AccessGraph.NewWorkspace($"Other {Guid.NewGuid():n}");
        var otherUser = AccessGraph.ActiveUser($"other-{Guid.NewGuid():n}@example.test");
        var otherMembership = AccessGraph.NewMembership(otherUser.Id, otherWorkspace.Id);

        // A PLATFORM role that happens to be named Member, carrying a platform permission and held by a user.
        var platformUser = AccessGraph.ActiveUser($"platform-{Guid.NewGuid():n}@example.test");
        var platformRole = AccessGraph.NewPlatformRole(MemberRoleName);
        var platformPermission = AccessGraph.NewPermission(PlatformReviewCode);

        await using (var seeding = Host.CreateVerificationContext())
        {
            seeding.Users.AddRange(otherUser, platformUser);
            seeding.Workspaces.Add(otherWorkspace);
            seeding.WorkspaceMemberships.Add(otherMembership);
            seeding.Roles.Add(platformRole);
            seeding.Permissions.Add(platformPermission);
            seeding.RolePermissions.Add(new RolePermission(platformRole.Id, platformPermission.Id));
            seeding.UserPlatformRoles.Add(new UserPlatformRole(platformUser.Id, platformRole.Id));
            await seeding.SaveChangesAsync();
        }

        var otherRoleId = await SeedMemberRoleAsync(otherWorkspace.Id, PropertyReadCode);

        await using (var seeding = Host.CreateVerificationContext())
        {
            seeding.WorkspaceMembershipRoles.Add(new WorkspaceMembershipRole(otherMembership.Id, otherRoleId));
            await seeding.SaveChangesAsync();
        }

        var result = await ApproveAsync(seeded.Request.Id, seeded.ReviewerA.Id);

        Assert.True(result.IsSuccess);

        var memberRoleId = await AssertCompliantMemberRoleAsync(seeded.Workspace.Id, expectedHolders: 1);
        Assert.NotEqual(otherRoleId, memberRoleId);
        Assert.NotEqual(platformRole.Id, memberRoleId);

        await using var verification = Host.CreateVerificationContext();

        // The other workspace's role: same single non-workspace.read link, same single assignment.
        var otherLink = await verification.RolePermissions.SingleAsync(row => row.RoleId == otherRoleId);
        Assert.Equal(PropertyReadCode, (await verification.Permissions.SingleAsync(row => row.Id == otherLink.PermissionId)).Code);
        Assert.Equal(1, await verification.WorkspaceMembershipRoles.CountAsync(row => row.RoleId == otherRoleId));

        // The platform role: still exactly its one platform permission and its one holder; nobody new holds it.
        var platformLink = await verification.RolePermissions.SingleAsync(row => row.RoleId == platformRole.Id);
        Assert.Equal(platformPermission.Id, platformLink.PermissionId);
        Assert.Equal(platformUser.Id, (await verification.UserPlatformRoles.SingleAsync()).UserId);

        // The platform permission is never linked to the new workspace Member role.
        Assert.False(await verification.RolePermissions.AnyAsync(
            row => row.RoleId == memberRoleId && row.PermissionId == platformPermission.Id));
    }

    [Fact]
    public async Task Approval_LeavesIndependentlyAssignedRolesUnchanged()
    {
        ReleaseMembershipLookupBarrier();
        var seeded = await SeedPendingRequestAsync();
        var membership = await SeedMembershipAsync(seeded.User.Id, seeded.Workspace.Id);

        var editor = AccessGraph.NewWorkspaceRole(seeded.Workspace.Id, "Editor");
        var propertyRead = AccessGraph.NewPermission(PropertyReadCode);

        await using (var seeding = Host.CreateVerificationContext())
        {
            seeding.Roles.Add(editor);
            seeding.Permissions.Add(propertyRead);
            seeding.RolePermissions.Add(new RolePermission(editor.Id, propertyRead.Id));
            seeding.WorkspaceMembershipRoles.Add(new WorkspaceMembershipRole(membership.Id, editor.Id));
            await seeding.SaveChangesAsync();
        }

        var result = await ApproveAsync(seeded.Request.Id, seeded.ReviewerA.Id);

        Assert.True(result.IsSuccess);
        Assert.Equal(membership.Id, result.Value.MembershipId);

        var memberRoleId = await AssertCompliantMemberRoleAsync(seeded.Workspace.Id, expectedHolders: 1);

        await using var verification = Host.CreateVerificationContext();

        // The membership now holds exactly Editor and Member; Editor's own permission set is untouched.
        var heldRoleIds = await verification.WorkspaceMembershipRoles
            .Where(row => row.WorkspaceMembershipId == membership.Id)
            .Select(row => row.RoleId)
            .ToListAsync();
        Assert.Equal(2, heldRoleIds.Count);
        Assert.Contains(editor.Id, heldRoleIds);
        Assert.Contains(memberRoleId, heldRoleIds);

        var editorLink = await verification.RolePermissions.SingleAsync(row => row.RoleId == editor.Id);
        Assert.Equal(propertyRead.Id, editorLink.PermissionId);
    }

    private static ApplicationDbContext ContextOf(IServiceScope scope)
    {
        return scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
    }

    // Awaits every participant, and opens the interceptor's gates the moment any of them faults or is canceled so
    // the others are not left waiting out the guard. The original failure is then rethrown by WhenAll.
    private async Task<T[]> WhenAllOrReleaseAsync<T>(params Task<T>[] tasks)
    {
        var pending = tasks.Cast<Task>().ToList();

        try
        {
            while (pending.Count > 0)
            {
                var done = await Task.WhenAny(pending);
                pending.Remove(done);

                if (done.IsFaulted || done.IsCanceled)
                {
                    saves.Release();
                }
            }
        }
        finally
        {
            saves.Release();
        }

        return await Task.WhenAll(tasks);
    }

    private static async Task<Permission> FindOrAddPermissionAsync(ApplicationDbContext context, string code)
    {
        var existing = await context.Permissions.SingleOrDefaultAsync(row => row.Code == code);

        if (existing is not null)
        {
            return existing;
        }

        var permission = AccessGraph.NewPermission(code);
        context.Permissions.Add(permission);

        return permission;
    }

    private async Task EnsurePermissionAsync(string code)
    {
        await using var context = Host.CreateVerificationContext();
        await FindOrAddPermissionAsync(context, code);
        await context.SaveChangesAsync();
    }

    // Persists a Member role in the workspace carrying exactly the given permission codes (none for an empty role).
    private async Task<Guid> SeedMemberRoleAsync(Guid workspaceId, params string[] permissionCodes)
    {
        var role = AccessGraph.NewWorkspaceRole(workspaceId, MemberRoleName);

        await using var context = Host.CreateVerificationContext();
        context.Roles.Add(role);

        foreach (var code in permissionCodes)
        {
            var permission = await FindOrAddPermissionAsync(context, code);
            context.RolePermissions.Add(new RolePermission(role.Id, permission.Id));
        }

        await context.SaveChangesAsync();

        return role.Id;
    }

    private async Task<WorkspaceMembership> SeedMembershipAsync(Guid userId, Guid workspaceId)
    {
        var membership = AccessGraph.NewMembership(userId, workspaceId);

        await using var context = Host.CreateVerificationContext();
        context.WorkspaceMemberships.Add(membership);
        await context.SaveChangesAsync();

        return membership;
    }

    // A second applicant for the same workspace as an already-seeded request.
    private async Task<(User User, WorkspaceAccessRequest Request)> SeedPendingApplicantAsync(Guid workspaceId)
    {
        var user = AccessGraph.PendingUser($"applicant-{Guid.NewGuid():n}@example.test");
        var request = new WorkspaceAccessRequest(Guid.NewGuid(), user.Id, workspaceId, TestClock.DefaultNow);

        await using var context = Host.CreateVerificationContext();
        context.Users.Add(user);
        context.WorkspaceAccessRequests.Add(request);
        await context.SaveChangesAsync();

        return (user, request);
    }

    // The first membership lookup of the host is paused for the older lookup-race test; these tests do not use it.
    private void ReleaseMembershipLookupBarrier()
    {
        membershipLookupBarrier.Release();
    }

    private ApproveWorkspaceAccessRequestCommandHandler CreateApproveHandler(IServiceScope scope, Guid reviewerId)
    {
        return new ApproveWorkspaceAccessRequestCommandHandler(
            new AuthenticatedCurrentUser(reviewerId),
            scope.ServiceProvider.GetRequiredService<IWorkspaceAccessRequestRepository>(),
            scope.ServiceProvider.GetRequiredService<IUserRepository>(),
            scope.ServiceProvider.GetRequiredService<IWorkspaceMembershipRepository>(),
            scope.ServiceProvider.GetRequiredService<IRoleRepository>(),
            scope.ServiceProvider.GetRequiredService<IPermissionRepository>(),
            Host.Clock,
            scope.ServiceProvider.GetRequiredService<IUnitOfWork>(),
            scope.ServiceProvider.GetRequiredService<IWorkspaceApprovalCoordinator>());
    }

    private async Task<Result<ApproveWorkspaceAccessRequestResult>> ApproveAsync(Guid requestId, Guid reviewerId)
    {
        using var scope = Host.CreateScope();

        return await CreateApproveHandler(scope, reviewerId).Handle(new ApproveWorkspaceAccessRequestCommand(requestId));
    }

    private static void AssertMemberRoleMisconfigured(Result<ApproveWorkspaceAccessRequestResult> result)
    {
        Assert.True(result.IsFailure);
        Assert.Equal("workspace_access_requests.member_role_misconfigured", result.Error!.Code);
        Assert.Equal(ErrorType.Conflict, result.Error.Type);
    }

    // The persisted state of a request that must not have been approved in any respect.
    private async Task AssertNothingApprovedAsync(SeededReview seeded)
    {
        await using var verification = Host.CreateVerificationContext();
        var request = await verification.WorkspaceAccessRequests.SingleAsync(row => row.Id == seeded.Request.Id);

        Assert.Equal(WorkspaceAccessRequestStatus.Pending, request.Status);
        Assert.Null(request.ReviewedByUserId);
        Assert.Null(request.ReviewedAt);
        Assert.Equal(UserStatus.Pending, (await verification.Users.SingleAsync(row => row.Id == seeded.User.Id)).Status);
        Assert.False(await verification.WorkspaceMemberships.AnyAsync(row => row.UserId == seeded.User.Id));
    }

    // One approved request, an Active user, one membership, and that membership holds the Member role once.
    private async Task AssertApprovedAsync(Guid requestId, Guid userId, Guid workspaceId)
    {
        await using var verification = Host.CreateVerificationContext();
        var request = await verification.WorkspaceAccessRequests.SingleAsync(row => row.Id == requestId);

        Assert.Equal(WorkspaceAccessRequestStatus.Approved, request.Status);
        Assert.Equal(UserStatus.Active, (await verification.Users.SingleAsync(row => row.Id == userId)).Status);

        var membership = await verification.WorkspaceMemberships.SingleAsync(
            row => row.UserId == userId && row.WorkspaceId == workspaceId);
        var memberRole = await verification.Roles.SingleAsync(
            row => row.WorkspaceId == workspaceId && row.Scope == RoleScope.Workspace && row.Name == MemberRoleName);

        Assert.Equal(1, await verification.WorkspaceMembershipRoles.CountAsync(
            row => row.WorkspaceMembershipId == membership.Id && row.RoleId == memberRole.Id));
    }

    // Exactly one Member role in the workspace, carrying exactly one link — to the one workspace.read permission —
    // held by the expected number of memberships, with no platform assignment written. Returns the role's id.
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
        Assert.Equal(1, await verification.Permissions.CountAsync(row => row.Code == WorkspaceReadCode));
        Assert.Equal(
            expectedHolders,
            await verification.WorkspaceMembershipRoles.CountAsync(row => row.RoleId == memberRole.Id));
        Assert.False(await verification.UserPlatformRoles.AnyAsync(row => row.RoleId == memberRole.Id));

        return memberRole.Id;
    }

    private static async Task<WorkspaceAccessRequest> GetRequestAsync(IServiceScope scope, Guid requestId)
    {
        return (await scope.ServiceProvider
            .GetRequiredService<IWorkspaceAccessRequestRepository>()
            .GetByIdAsync(requestId))!;
    }

    private static async Task<User> GetUserAsync(IServiceScope scope, Guid userId)
    {
        return (await scope.ServiceProvider.GetRequiredService<IUserRepository>().GetByIdAsync(userId))!;
    }

    private static Task<int> SaveAsync(IServiceScope scope)
    {
        return scope.ServiceProvider.GetRequiredService<IUnitOfWork>().SaveChangesAsync();
    }

    private static void AssertControlledApprovalConflict(Exception? exception)
    {
        switch (exception)
        {
            case ConcurrencyConflictException concurrencyConflict:
                Assert.Equal(PersistenceResource.WorkspaceAccessRequest, concurrencyConflict.Resource);
                break;
            case UniqueConstraintViolationException uniqueConflict:
                Assert.Equal(PersistenceConstraint.WorkspaceMembershipUserWorkspace, uniqueConflict.Constraint);
                break;
            default:
                Assert.Fail($"Expected a recognized review conflict, but received {exception?.GetType().Name ?? "no exception"}.");
                break;
        }
    }

    private async Task<SeededReview> SeedPendingRequestAsync()
    {
        var user = AccessGraph.PendingUser($"applicant-{Guid.NewGuid():n}@example.test");
        var workspace = AccessGraph.NewWorkspace($"Workspace {Guid.NewGuid():n}");
        var request = new WorkspaceAccessRequest(Guid.NewGuid(), user.Id, workspace.Id, TestClock.DefaultNow);
        var reviewerA = AccessGraph.ActiveUser($"reviewer-a-{Guid.NewGuid():n}@example.test");
        var reviewerB = AccessGraph.ActiveUser($"reviewer-b-{Guid.NewGuid():n}@example.test");

        await using var context = Host.CreateVerificationContext();
        context.Users.AddRange(user, reviewerA, reviewerB);
        context.Workspaces.Add(workspace);
        context.WorkspaceAccessRequests.Add(request);
        await context.SaveChangesAsync();

        return new SeededReview(user, workspace, request, reviewerA, reviewerB);
    }

    private sealed record SeededReview(
        User User,
        Workspace Workspace,
        WorkspaceAccessRequest Request,
        User ReviewerA,
        User ReviewerB);

    /// <summary>
    /// Disarmed it does nothing, so every older test in this class is unaffected. Armed, it acts only on the
    /// exact <see cref="DbContext"/> instances it was given (compared by reference), so seeding and verification
    /// contexts — which carry no interceptors at all — and any unarmed handler scope are never held or recorded.
    /// </summary>
    /// <remarks>
    /// It delays <c>SaveChangesAsync</c>; it never replaces it, so which writer wins is decided by PostgreSQL.
    /// A barrier holds the first save of each listed context until all have arrived; a pause holds the first save
    /// of one context until <see cref="Release"/>. Only a context's FIRST save waits: a recovery save by the same
    /// context passes straight through. Every failed save of an armed context is recorded: by the constraint
    /// PostgreSQL reported, or as "concurrency" when the request's status token was lost. A test therefore proves
    /// the collision happened rather than inferring it from a successful outcome. The guard is a failsafe against
    /// a mis-wired test, never synchronisation; a timeout or cancellation surfaces as an exception and fails the
    /// test.
    /// </remarks>
    private sealed class ControlledSaveInterceptor : SaveChangesInterceptor
    {
        private static readonly TimeSpan DeadlockGuard = TimeSpan.FromSeconds(60);

        private readonly object gate = new();
        private readonly ConcurrentQueue<string> failures = new();

        private HashSet<DbContext> barrierTargets = [];
        private HashSet<DbContext> barrierArrived = [];
        private TaskCompletionSource? barrierOpen;

        private DbContext? pauseTarget;
        private bool pauseTaken;
        private TaskCompletionSource? paused;
        private TaskCompletionSource? pauseOpen;

        /// <summary>
        /// One entry per failed save of an armed context: the PostgreSQL constraint name (or SQLSTATE), "concurrency"
        /// for a lost concurrency token, or the exception type name.
        /// </summary>
        public IReadOnlyCollection<string> Failures => failures;

        public void ArmBarrier(params DbContext[] contexts)
        {
            ArgumentNullException.ThrowIfNull(contexts);

            if (contexts.Length < 2)
            {
                throw new ArgumentException("A barrier needs at least two participants.", nameof(contexts));
            }

            if (contexts.Any(context => context is null))
            {
                throw new ArgumentException("A barrier participant must not be null.", nameof(contexts));
            }

            var targets = new HashSet<DbContext>(ReferenceEqualityComparer.Instance);

            foreach (var context in contexts)
            {
                if (!targets.Add(context))
                {
                    throw new ArgumentException("A barrier participant must not be listed twice.", nameof(contexts));
                }
            }

            lock (gate)
            {
                barrierTargets = targets;
                barrierArrived = new HashSet<DbContext>(ReferenceEqualityComparer.Instance);
                barrierOpen = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            }
        }

        public void ArmPause(DbContext context)
        {
            ArgumentNullException.ThrowIfNull(context);

            lock (gate)
            {
                pauseTarget = context;
                pauseTaken = false;
                paused = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                pauseOpen = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            }
        }

        public Task WaitUntilPausedAsync()
        {
            Task task;

            lock (gate)
            {
                task = (paused ?? throw new InvalidOperationException("ArmPause was not called.")).Task;
            }

            return task.WaitAsync(DeadlockGuard);
        }

        /// <summary>Opens every gate and disarms the pause. Idempotent; safe in a finally. Targets are kept.</summary>
        public void Release()
        {
            lock (gate)
            {
                pauseTaken = true;
                pauseOpen?.TrySetResult();
                barrierOpen?.TrySetResult();
            }
        }

        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            Task? wait = null;

            lock (gate)
            {
                var context = eventData.Context;

                if (context is not null && barrierOpen is not null && barrierTargets.Contains(context))
                {
                    // Only a context's first save counts and waits; a recovery save by the same context passes.
                    if (barrierArrived.Add(context))
                    {
                        if (barrierArrived.Count == barrierTargets.Count)
                        {
                            barrierOpen.TrySetResult();
                        }

                        wait = barrierOpen.Task;
                    }
                }
                else if (context is not null && ReferenceEquals(context, pauseTarget) && !pauseTaken)
                {
                    pauseTaken = true;
                    paused!.TrySetResult();
                    wait = pauseOpen!.Task;
                }
            }

            if (wait is not null)
            {
                await wait.WaitAsync(DeadlockGuard, cancellationToken);
            }

            return result;
        }

        public override Task SaveChangesFailedAsync(
            DbContextErrorEventData eventData,
            CancellationToken cancellationToken = default)
        {
            // EF Core 10 does not report a DbUpdateConcurrencyException here; it takes the dedicated path below.
            if (IsArmed(eventData.Context))
            {
                failures.Enqueue(eventData.Exception switch
                {
                    DbUpdateException { InnerException: PostgresException postgres } =>
                        postgres.ConstraintName ?? postgres.SqlState,
                    var other => other.GetType().Name
                });
            }

            return Task.CompletedTask;
        }

        // EF calls this immediately before it throws a DbUpdateConcurrencyException (a lost request-status token).
        // The result is passed through untouched, so the exception is still thrown and the handler sees exactly
        // what it would in production.
        public override ValueTask<InterceptionResult> ThrowingConcurrencyExceptionAsync(
            ConcurrencyExceptionEventData eventData,
            InterceptionResult result,
            CancellationToken cancellationToken = default)
        {
            if (IsArmed(eventData.Context))
            {
                failures.Enqueue("concurrency");
            }

            return new ValueTask<InterceptionResult>(result);
        }

        // Only explicitly armed contexts (barrier participants or the paused one), by reference.
        private bool IsArmed(DbContext? context)
        {
            lock (gate)
            {
                return context is not null
                    && (barrierTargets.Contains(context) || ReferenceEquals(context, pauseTarget));
            }
        }
    }

    /// <summary>
    /// Holds ONE explicitly armed context immediately BEFORE it sends its first <c>workspace_membership_roles</c>
    /// SELECT — in the approval handler, the holder check that follows the permission-set read — until released.
    /// Disarmed it does nothing, so every other test in this class is unaffected.
    /// </summary>
    /// <remarks>
    /// Pausing before the command is sent, rather than after it has run, is what fixes the interleaving: the
    /// handler has already awaited the preceding permission-set read to completion, so that result is a finished
    /// local value, and the statement that is held has not yet taken its snapshot. The target is matched by
    /// reference, so seeding and verification contexts (which carry no interceptors at all) and any other scope are
    /// never held. <see cref="PermissionSetReadsBeforePause"/> counts the completed <c>role_permissions</c> SELECTs
    /// of the target before the pause, as direct evidence that the first read really ran. The guard is a failsafe
    /// against a mis-wired test, never synchronisation; a timeout or cancellation throws and fails the test.
    /// </remarks>
    private sealed class SecondPolicyReadPause : DbCommandInterceptor
    {
        private static readonly TimeSpan DeadlockGuard = TimeSpan.FromSeconds(60);

        private readonly object gate = new();

        private DbContext? target;
        private bool taken;
        private int permissionSetReads;
        private TaskCompletionSource paused = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private TaskCompletionSource open = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Completed <c>role_permissions</c> SELECTs of the armed context before it was held.</summary>
        public int PermissionSetReadsBeforePause { get; private set; }

        public void Arm(DbContext context)
        {
            ArgumentNullException.ThrowIfNull(context);

            lock (gate)
            {
                target = context;
                taken = false;
                permissionSetReads = 0;
                PermissionSetReadsBeforePause = 0;
                paused = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                open = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            }
        }

        public Task WaitUntilPausedAsync()
        {
            Task task;

            lock (gate)
            {
                task = paused.Task;
            }

            return task.WaitAsync(DeadlockGuard);
        }

        /// <summary>Opens the gate and disarms the pause. Idempotent; safe in a finally.</summary>
        public void Release()
        {
            lock (gate)
            {
                taken = true;
                open.TrySetResult();
            }
        }

        public override ValueTask<DbDataReader> ReaderExecutedAsync(
            DbCommand command,
            CommandExecutedEventData eventData,
            DbDataReader result,
            CancellationToken cancellationToken = default)
        {
            lock (gate)
            {
                if (!taken && IsTarget(eventData) && IsSelectFrom(command, "role_permissions"))
                {
                    permissionSetReads++;
                }
            }

            return new ValueTask<DbDataReader>(result);
        }

        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            Task? wait = null;

            lock (gate)
            {
                if (!taken && IsTarget(eventData) && IsSelectFrom(command, "workspace_membership_roles"))
                {
                    taken = true;
                    PermissionSetReadsBeforePause = permissionSetReads;
                    paused.TrySetResult();
                    wait = open.Task;
                }
            }

            if (wait is not null)
            {
                await wait.WaitAsync(DeadlockGuard, cancellationToken);
            }

            return result;
        }

        private bool IsTarget(CommandEventData eventData)
        {
            return target is not null && ReferenceEquals(eventData.Context, target);
        }

        private static bool IsSelectFrom(DbCommand command, string table)
        {
            return command.CommandText.TrimStart().StartsWith("SELECT", StringComparison.OrdinalIgnoreCase)
                && command.CommandText.Contains(table, StringComparison.OrdinalIgnoreCase);
        }
    }

    private sealed class AuthenticatedCurrentUser(Guid userId) : ICurrentUser
    {
        public Guid? UserId { get; } = userId;

        public bool IsAuthenticated => true;
    }
}
