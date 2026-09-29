using Microsoft.Extensions.DependencyInjection;
using SmartProperty.Application.Abstractions.Persistence;
using SmartProperty.Application.Abstractions.Persistence.Repositories;
using SmartProperty.Common.Pagination;
using SmartProperty.Domain.Identity;
using SmartProperty.Domain.Workspaces;
using SmartProperty.Persistence.IntegrationTests.Infrastructure;
using Xunit;

namespace SmartProperty.Persistence.IntegrationTests.Persistence;

/// <summary>
/// Round-trips the repository methods the review and bootstrap use cases depend on, through real PostgreSQL.
/// </summary>
/// <remarks>
/// The list query in particular is worth running against the real provider rather than a fake: it projects a read
/// model across two joins after paging, and whether that translates to SQL at all is not something an in-memory
/// double can tell anyone.
/// </remarks>
public sealed class WorkspaceAccessRequestReviewRepositoryTests(PostgreSqlFixture fixture) : DatabaseTest(fixture)
{
    private const string ReviewPermissionCode = "workspace.access_requests.review";

    [Fact]
    public async Task ThePendingQueueCarriesTheApplicantAndTheWorkspaceInOneRow()
    {
        var (user, workspace, request) = await SeedRequestAsync();

        using var scope = Host.CreateScope();
        var accessRequests = scope.ServiceProvider.GetRequiredService<IWorkspaceAccessRequestRepository>();

        var page = await accessRequests.ListAsync(
            WorkspaceAccessRequestStatus.Pending,
            workspaceId: null,
            skip: 0,
            take: 10);

        Assert.Equal(1, page.TotalCount);
        var item = Assert.Single(page.Items);
        Assert.Equal(request.Id, item.RequestId);
        Assert.Equal(user.Id, item.UserId);
        Assert.Equal(user.Email, item.UserEmail);
        Assert.Equal(user.FirstName, item.UserFirstName);
        Assert.Equal(user.LastName, item.UserLastName);
        Assert.Equal(workspace.Id, item.WorkspaceId);
        Assert.Equal(workspace.Name, item.WorkspaceName);
        Assert.Equal(WorkspaceAccessRequestStatus.Pending, item.Status);
        Assert.Equal(TestClock.DefaultNow, item.RequestedAt);
        Assert.Null(item.ReviewedAt);
        Assert.Null(item.ReviewedByUserId);
    }

    [Fact]
    public async Task TheQueueReturnsOnlyTheRequestedStatus()
    {
        var (_, workspace, pending) = await SeedRequestAsync();
        var reviewer = AccessGraph.ActiveUser("reviewer@example.test");
        var rejectedApplicant = AccessGraph.PendingUser("rejected.applicant@example.test");
        var rejected = new WorkspaceAccessRequest(
            Guid.NewGuid(),
            rejectedApplicant.Id,
            workspace.Id,
            TestClock.DefaultNow);
        rejected.Reject(reviewer.Id, TestClock.DefaultNow);

        await using (var seeding = Host.CreateVerificationContext())
        {
            seeding.Users.Add(reviewer);
            seeding.Users.Add(rejectedApplicant);
            seeding.WorkspaceAccessRequests.Add(rejected);
            await seeding.SaveChangesAsync();
        }

        using var scope = Host.CreateScope();
        var accessRequests = scope.ServiceProvider.GetRequiredService<IWorkspaceAccessRequestRepository>();

        var pendingPage = await accessRequests.ListAsync(
            WorkspaceAccessRequestStatus.Pending,
            workspaceId: null,
            skip: 0,
            take: 10);
        var rejectedPage = await accessRequests.ListAsync(
            WorkspaceAccessRequestStatus.Rejected,
            workspaceId: null,
            skip: 0,
            take: 10);

        Assert.Equal(pending.Id, Assert.Single(pendingPage.Items).RequestId);

        var rejectedItem = Assert.Single(rejectedPage.Items);
        Assert.Equal(rejected.Id, rejectedItem.RequestId);
        Assert.Equal(reviewer.Id, rejectedItem.ReviewedByUserId);
        Assert.Equal(TestClock.DefaultNow, rejectedItem.ReviewedAt);
    }

    [Fact]
    public async Task TheQueueCanBeNarrowedToOneWorkspace()
    {
        var (_, wanted, wantedRequest) = await SeedRequestAsync();
        var (_, other, _) = await SeedRequestAsync();

        using var scope = Host.CreateScope();
        var accessRequests = scope.ServiceProvider.GetRequiredService<IWorkspaceAccessRequestRepository>();

        var page = await accessRequests.ListAsync(
            WorkspaceAccessRequestStatus.Pending,
            wanted.Id,
            skip: 0,
            take: 10);

        Assert.Equal(1, page.TotalCount);
        Assert.Equal(wantedRequest.Id, Assert.Single(page.Items).RequestId);
        Assert.NotEqual(wanted.Id, other.Id);
    }

    [Fact]
    public async Task PagingWindowsTheQueueWhileTheTotalStillCountsEverything()
    {
        await SeedRequestAsync();
        await SeedRequestAsync();
        await SeedRequestAsync();

        using var scope = Host.CreateScope();
        var accessRequests = scope.ServiceProvider.GetRequiredService<IWorkspaceAccessRequestRepository>();

        var first = await accessRequests.ListAsync(
            WorkspaceAccessRequestStatus.Pending,
            workspaceId: null,
            skip: 0,
            take: 2);
        var second = await accessRequests.ListAsync(
            WorkspaceAccessRequestStatus.Pending,
            workspaceId: null,
            skip: 2,
            take: 2);

        Assert.Equal(3, first.TotalCount);
        Assert.Equal(3, second.TotalCount);
        Assert.Equal(2, first.Items.Count);
        Assert.Single(second.Items);

        // The ordering is stable, so no request appears on both pages and none is skipped.
        var seen = first.Items.Concat(second.Items).Select(item => item.RequestId).ToArray();
        Assert.Equal(3, seen.Distinct().Count());
    }

    [Fact]
    public async Task APageBeyondTheLast_ReturnsEmptyWhilePreservingTheTotal()
    {
        await SeedRequestAsync();

        using var scope = Host.CreateScope();
        var accessRequests = scope.ServiceProvider.GetRequiredService<IWorkspaceAccessRequestRepository>();

        var page = await accessRequests.ListAsync(
            WorkspaceAccessRequestStatus.Pending,
            workspaceId: null,
            skip: PageParameters.MaxOffset,
            take: PageParameters.MaxPageSize);

        Assert.Empty(page.Items);
        Assert.Equal(1, page.TotalCount);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(PageParameters.MaxOffset + 1)]
    public async Task InvalidSkip_IsRejectedBeforeQueryingPostgreSql(int skip)
    {
        using var scope = Host.CreateScope();
        var accessRequests = scope.ServiceProvider.GetRequiredService<IWorkspaceAccessRequestRepository>();

        var exception = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            accessRequests.ListAsync(
                WorkspaceAccessRequestStatus.Pending,
                workspaceId: null,
                skip,
                take: 10));

        Assert.Equal("skip", exception.ParamName);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(PageParameters.MaxPageSize + 1)]
    [InlineData(int.MaxValue)]
    public async Task InvalidTake_IsRejectedBeforeQueryingPostgreSql(int take)
    {
        using var scope = Host.CreateScope();
        var accessRequests = scope.ServiceProvider.GetRequiredService<IWorkspaceAccessRequestRepository>();

        var exception = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            accessRequests.ListAsync(
                WorkspaceAccessRequestStatus.Pending,
                workspaceId: null,
                skip: 0,
                take));

        Assert.Equal("take", exception.ParamName);
    }

    [Fact]
    public async Task AnApprovalWrittenInOneScopeIsReadBackInAnother()
    {
        var (user, workspace, request) = await SeedRequestAsync();
        var reviewer = AccessGraph.ActiveUser("approving.reviewer@example.test");

        await using (var seeding = Host.CreateVerificationContext())
        {
            seeding.Users.Add(reviewer);
            await seeding.SaveChangesAsync();
        }

        var reviewedAt = TestClock.DefaultNow.AddHours(1);

        using (var writing = Host.CreateScope())
        {
            var accessRequests = writing.ServiceProvider.GetRequiredService<IWorkspaceAccessRequestRepository>();
            var memberships = writing.ServiceProvider.GetRequiredService<IWorkspaceMembershipRepository>();
            var users = writing.ServiceProvider.GetRequiredService<IUserRepository>();

            var tracked = await accessRequests.GetByIdAsync(request.Id);
            tracked!.Approve(reviewer.Id, reviewedAt);

            var trackedUser = await users.GetByIdAsync(user.Id);
            trackedUser!.Activate(reviewedAt);

            await memberships.AddAsync(
                new WorkspaceMembership(Guid.NewGuid(), user.Id, workspace.Id, reviewedAt));

            await writing.ServiceProvider.GetRequiredService<IUnitOfWork>().SaveChangesAsync();
        }

        using var reading = Host.CreateScope();
        var persisted = await reading.ServiceProvider
            .GetRequiredService<IWorkspaceAccessRequestRepository>()
            .GetByIdAsync(request.Id);
        var membership = await reading.ServiceProvider
            .GetRequiredService<IWorkspaceMembershipRepository>()
            .GetByUserAndWorkspaceAsync(user.Id, workspace.Id);
        var persistedUser = await reading.ServiceProvider
            .GetRequiredService<IUserRepository>()
            .GetByIdAsync(user.Id);

        Assert.Equal(WorkspaceAccessRequestStatus.Approved, persisted!.Status);
        Assert.Equal(reviewer.Id, persisted.ReviewedByUserId);
        Assert.Equal(reviewedAt, persisted.ReviewedAt);
        Assert.NotNull(membership);
        Assert.Equal(UserStatus.Active, persistedUser!.Status);

        await using var verification = Host.CreateVerificationContext();
        Assert.Empty(verification.WorkspaceMembershipRoles);
    }

    [Fact]
    public async Task APlatformRoleIsFoundByNameAndAWorkspaceRoleSharingItIsNot()
    {
        var workspace = AccessGraph.NewWorkspace();
        var platformRole = AccessGraph.NewPlatformRole("Platform Admin");
        var workspaceRole = AccessGraph.NewWorkspaceRole(workspace.Id, "Platform Admin");

        await using (var seeding = Host.CreateVerificationContext())
        {
            seeding.Workspaces.Add(workspace);
            seeding.Roles.Add(platformRole);
            seeding.Roles.Add(workspaceRole);
            await seeding.SaveChangesAsync();
        }

        using var scope = Host.CreateScope();
        var roles = scope.ServiceProvider.GetRequiredService<IRoleRepository>();

        var found = await roles.GetPlatformRoleByNameAsync("  Platform Admin  ");

        Assert.NotNull(found);
        Assert.Equal(platformRole.Id, found.Id);
        Assert.Null(found.WorkspaceId);
    }

    [Fact]
    public async Task AnUnknownPlatformRoleNameResolvesToNothing()
    {
        using var scope = Host.CreateScope();
        var roles = scope.ServiceProvider.GetRequiredService<IRoleRepository>();

        Assert.Null(await roles.GetPlatformRoleByNameAsync("No Such Role"));
    }

    [Fact]
    public async Task RolePermissionAndPlatformAssignmentAreSeenOnlyOnceWritten()
    {
        var user = AccessGraph.PendingUser("provisioned.admin@example.test");
        var role = AccessGraph.NewPlatformRole("Platform Admin");
        var permission = AccessGraph.NewPermission(ReviewPermissionCode);

        await using (var seeding = Host.CreateVerificationContext())
        {
            seeding.Users.Add(user);
            seeding.Roles.Add(role);
            seeding.Permissions.Add(permission);
            await seeding.SaveChangesAsync();
        }

        using (var before = Host.CreateScope())
        {
            var roles = before.ServiceProvider.GetRequiredService<IRoleRepository>();

            Assert.False(await roles.HasPermissionAsync(role.Id, permission.Id));
            Assert.False(await roles.IsAssignedToUserAsync(user.Id, role.Id));
        }

        using (var writing = Host.CreateScope())
        {
            var roles = writing.ServiceProvider.GetRequiredService<IRoleRepository>();

            await roles.AddPermissionAsync(new RolePermission(role.Id, permission.Id));
            await roles.AddUserAssignmentAsync(new UserPlatformRole(user.Id, role.Id));

            await writing.ServiceProvider.GetRequiredService<IUnitOfWork>().SaveChangesAsync();
        }

        using var after = Host.CreateScope();
        var persistedRoles = after.ServiceProvider.GetRequiredService<IRoleRepository>();

        Assert.True(await persistedRoles.HasPermissionAsync(role.Id, permission.Id));
        Assert.True(await persistedRoles.IsAssignedToUserAsync(user.Id, role.Id));
    }

    /// <summary>
    /// A pending request for a fresh applicant in a fresh workspace, persisted outside the container.
    /// </summary>
    private async Task<(User User, Workspace Workspace, WorkspaceAccessRequest Request)> SeedRequestAsync()
    {
        var user = AccessGraph.PendingUser($"applicant-{Guid.NewGuid():n}@example.test");
        var workspace = AccessGraph.NewWorkspace($"Workspace {Guid.NewGuid():n}");
        var request = new WorkspaceAccessRequest(Guid.NewGuid(), user.Id, workspace.Id, TestClock.DefaultNow);

        await using var context = Host.CreateVerificationContext();
        context.Users.Add(user);
        context.Workspaces.Add(workspace);
        context.WorkspaceAccessRequests.Add(request);
        await context.SaveChangesAsync();

        return (user, workspace, request);
    }
}
