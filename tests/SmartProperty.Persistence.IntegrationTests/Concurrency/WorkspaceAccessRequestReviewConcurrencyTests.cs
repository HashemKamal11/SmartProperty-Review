using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SmartProperty.Application.Abstractions.Identity;
using SmartProperty.Application.Abstractions.Persistence;
using SmartProperty.Application.Abstractions.Persistence.Repositories;
using SmartProperty.Application.WorkspaceAccessRequests.Approve;
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
    private readonly WorkspaceMembershipLookupBarrier membershipLookupBarrier = new();

    private protected override IInterceptor[] ExtraInterceptors()
    {
        return [membershipLookupBarrier];
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

    [Fact]
    public async Task ExternalMembershipCreationWhileApprovalIsInFlight_ReusesMembershipAndCompletesApproval()
    {
        var seeded = await SeedPendingRequestAsync();
        var reviewedAt = TestClock.DefaultNow.AddMinutes(1);
        Host.Clock.UtcNow = reviewedAt;

        await using (var initialState = Host.CreateVerificationContext())
        {
            Assert.Equal(
                WorkspaceAccessRequestStatus.Pending,
                (await initialState.WorkspaceAccessRequests.SingleAsync(row => row.Id == seeded.Request.Id)).Status);
            Assert.False(await initialState.WorkspaceMemberships.AnyAsync(row =>
                row.UserId == seeded.User.Id && row.WorkspaceId == seeded.Workspace.Id));
        }

        using var approvingScope = Host.CreateScope();
        var handler = new ApproveWorkspaceAccessRequestCommandHandler(
            new AuthenticatedCurrentUser(seeded.ReviewerA.Id),
            approvingScope.ServiceProvider.GetRequiredService<IWorkspaceAccessRequestRepository>(),
            approvingScope.ServiceProvider.GetRequiredService<IUserRepository>(),
            approvingScope.ServiceProvider.GetRequiredService<IWorkspaceMembershipRepository>(),
            Host.Clock,
            approvingScope.ServiceProvider.GetRequiredService<IUnitOfWork>());

        var approval = handler.Handle(new ApproveWorkspaceAccessRequestCommand(seeded.Request.Id));

        // The intercepted SELECT has already completed with no row, so A has observed the membership as absent.
        // Keep A paused while B commits the exact user/workspace membership through an independent DbContext.
        await membershipLookupBarrier.WaitUntilLookupCompletesAsync();
        var externalMembership = new WorkspaceMembership(
            Guid.NewGuid(),
            seeded.User.Id,
            seeded.Workspace.Id,
            reviewedAt.AddSeconds(-1));

        try
        {
            await using var independentWriter = Host.CreateVerificationContext();
            independentWriter.WorkspaceMemberships.Add(externalMembership);
            await independentWriter.SaveChangesAsync();
        }
        finally
        {
            membershipLookupBarrier.Release();
        }

        var result = await approval;

        Assert.True(result.IsSuccess);
        Assert.Equal(externalMembership.Id, result.Value.MembershipId);

        await using var verification = Host.CreateVerificationContext();
        var persistedRequest = await verification.WorkspaceAccessRequests.SingleAsync(
            row => row.Id == seeded.Request.Id);
        var persistedUser = await verification.Users.SingleAsync(row => row.Id == seeded.User.Id);
        var persistedMemberships = await verification.WorkspaceMemberships
            .Where(row => row.UserId == seeded.User.Id && row.WorkspaceId == seeded.Workspace.Id)
            .ToListAsync();

        Assert.Equal(WorkspaceAccessRequestStatus.Approved, persistedRequest.Status);
        Assert.Equal(seeded.ReviewerA.Id, persistedRequest.ReviewedByUserId);
        Assert.Equal(reviewedAt, persistedRequest.ReviewedAt);
        Assert.Equal(UserStatus.Active, persistedUser.Status);
        Assert.Equal(externalMembership.Id, Assert.Single(persistedMemberships).Id);
        Assert.Equal(0, await verification.WorkspaceMembershipRoles.CountAsync());
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

    private sealed class AuthenticatedCurrentUser(Guid userId) : ICurrentUser
    {
        public Guid? UserId { get; } = userId;

        public bool IsAuthenticated => true;
    }
}
