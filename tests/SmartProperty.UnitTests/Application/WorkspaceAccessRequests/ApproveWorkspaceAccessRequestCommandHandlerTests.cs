using SmartProperty.Application.Abstractions.Persistence;
using SmartProperty.Application.Authorization;
using SmartProperty.Application.WorkspaceAccessRequests;
using SmartProperty.Application.WorkspaceAccessRequests.Approve;
using SmartProperty.Application.Workspaces;
using SmartProperty.Domain.Identity;
using SmartProperty.Domain.Workspaces;
using SmartProperty.UnitTests.TestDoubles;
using Xunit;

namespace SmartProperty.UnitTests.Application.WorkspaceAccessRequests;

/// <summary>
/// Exercises the real <see cref="ApproveWorkspaceAccessRequestCommandHandler"/>: what an approval changes, what it
/// deliberately leaves alone, and which second attempt is refused.
/// </summary>
/// <remarks>
/// The reviewer only ever arrives through <c>ICurrentUser</c>; the command carries no reviewer field for a test to
/// set, which is the point. Approval also gives the membership the workspace's Member role, which carries only
/// <c>workspace.read</c>.
/// </remarks>
public sealed class ApproveWorkspaceAccessRequestCommandHandlerTests
{
    private static readonly DateTimeOffset RequestedAt = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset ReviewedAt = new(2026, 2, 1, 9, 30, 0, TimeSpan.Zero);

    private readonly FakeWorkspaceAccessRequestRepository _accessRequests = new();
    private readonly FakeUserRepository _users = new();
    private readonly FakeWorkspaceMembershipRepository _memberships = new();
    private readonly FakeRoleRepository _roles = new();
    private readonly FakePermissionRepository _permissions = new();
    private readonly FakeDateTimeProvider _clock = new(ReviewedAt);
    private readonly FakeUnitOfWork _unitOfWork = new();
    private readonly FakeWorkspaceApprovalCoordinator _coordinator = new();
    private readonly Guid _reviewerUserId = Guid.NewGuid();
    private readonly Guid _workspaceId = Guid.NewGuid();

    [Fact]
    public async Task NoCurrentUser_ReturnsUnauthorized()
    {
        var handler = CreateHandler(new FakeCurrentUser(null));

        var result = await handler.Handle(new ApproveWorkspaceAccessRequestCommand(Guid.NewGuid()));

        Assert.True(result.IsFailure);
        Assert.Same(WorkspaceAccessRequestErrors.Unauthorized, result.Error);
        Assert.Equal(0, _accessRequests.GetByIdCallCount);
        Assert.Equal(0, _unitOfWork.SaveChangesCallCount);
    }

    [Fact]
    public async Task UnknownRequest_ReturnsNotFoundAndSavesNothing()
    {
        var handler = CreateHandler();

        var result = await handler.Handle(new ApproveWorkspaceAccessRequestCommand(Guid.NewGuid()));

        Assert.True(result.IsFailure);
        Assert.Same(WorkspaceAccessRequestErrors.NotFound, result.Error);
        Assert.Equal(0, _unitOfWork.SaveChangesCallCount);
        Assert.Empty(_memberships.Added);
    }

    [Fact]
    public async Task PendingRequest_IsApproved()
    {
        var (request, _) = Seed(UserStatus.Pending);
        var handler = CreateHandler();

        var result = await handler.Handle(new ApproveWorkspaceAccessRequestCommand(request.Id));

        Assert.True(result.IsSuccess);
        Assert.Equal(WorkspaceAccessRequestStatus.Approved, request.Status);
        Assert.Equal(WorkspaceAccessRequestStatus.Approved, result.Value.RequestStatus);
        Assert.Equal(1, _unitOfWork.SaveChangesCallCount);
    }

    [Fact]
    public async Task PendingRequest_StoresTheAuthenticatedReviewerAndTheCurrentTime()
    {
        var (request, _) = Seed(UserStatus.Pending);
        var handler = CreateHandler();

        await handler.Handle(new ApproveWorkspaceAccessRequestCommand(request.Id));

        Assert.Equal(_reviewerUserId, request.ReviewedByUserId);
        Assert.Equal(ReviewedAt, request.ReviewedAt);
    }

    [Fact]
    public async Task PendingUser_BecomesActive()
    {
        var (request, user) = Seed(UserStatus.Pending);
        var handler = CreateHandler();

        var result = await handler.Handle(new ApproveWorkspaceAccessRequestCommand(request.Id));

        Assert.Equal(UserStatus.Active, user.Status);
        Assert.Equal(UserStatus.Active, result.Value.UserStatus);
    }

    [Fact]
    public async Task ActiveUser_StaysActiveAndIsNotTouched()
    {
        var (request, user) = Seed(UserStatus.Active);
        var updatedAtBefore = user.UpdatedAt;
        var handler = CreateHandler();

        var result = await handler.Handle(new ApproveWorkspaceAccessRequestCommand(request.Id));

        Assert.True(result.IsSuccess);
        Assert.Equal(UserStatus.Active, user.Status);

        // An approval for a second workspace must not rewrite the account's own audit trail.
        Assert.Equal(updatedAtBefore, user.UpdatedAt);
    }

    [Theory]
    [InlineData(UserStatus.Suspended)]
    [InlineData(UserStatus.Deactivated)]
    public async Task DisabledUser_IsRefusedWithoutChangingAnything(UserStatus status)
    {
        var (request, user) = Seed(status);
        var handler = CreateHandler();

        var result = await handler.Handle(new ApproveWorkspaceAccessRequestCommand(request.Id));

        Assert.True(result.IsFailure);
        Assert.Same(WorkspaceAccessRequestErrors.TargetAccountUnavailable, result.Error);

        // Approval must never double as a way to re-enable a disabled account.
        Assert.Equal(status, user.Status);
        Assert.Equal(WorkspaceAccessRequestStatus.Pending, request.Status);
        Assert.Empty(_memberships.Added);
        Assert.Equal(0, _unitOfWork.SaveChangesCallCount);
    }

    [Fact]
    public async Task MissingUserRow_IsRefusedAsAnUnavailableAccount()
    {
        var request = new WorkspaceAccessRequest(Guid.NewGuid(), Guid.NewGuid(), _workspaceId, RequestedAt);
        _accessRequests.Seed(request);
        var handler = CreateHandler();

        var result = await handler.Handle(new ApproveWorkspaceAccessRequestCommand(request.Id));

        Assert.True(result.IsFailure);
        Assert.Same(WorkspaceAccessRequestErrors.TargetAccountUnavailable, result.Error);
        Assert.Equal(WorkspaceAccessRequestStatus.Pending, request.Status);
        Assert.Equal(0, _unitOfWork.SaveChangesCallCount);
    }

    [Fact]
    public async Task Approval_CreatesTheWorkspaceMembership()
    {
        var (request, user) = Seed(UserStatus.Pending);
        var handler = CreateHandler();

        var result = await handler.Handle(new ApproveWorkspaceAccessRequestCommand(request.Id));

        var membership = Assert.Single(_memberships.Added);
        Assert.Equal(user.Id, membership.UserId);
        Assert.Equal(_workspaceId, membership.WorkspaceId);
        Assert.Equal(ReviewedAt, membership.CreatedAt);
        Assert.Equal(membership.Id, result.Value.MembershipId);
    }

    [Fact]
    public async Task ExistingMembership_IsReusedRatherThanDuplicated()
    {
        var (request, user) = Seed(UserStatus.Active);
        var existing = new WorkspaceMembership(Guid.NewGuid(), user.Id, _workspaceId, RequestedAt);
        _memberships.Seed(existing);
        var handler = CreateHandler();

        var result = await handler.Handle(new ApproveWorkspaceAccessRequestCommand(request.Id));

        Assert.True(result.IsSuccess);
        Assert.Empty(_memberships.Added);
        Assert.Equal(existing.Id, result.Value.MembershipId);
    }

    [Fact]
    public async Task Approval_CommitsEveryChangeInOneUnitOfWork()
    {
        var (request, _) = Seed(UserStatus.Pending);
        var handler = CreateHandler();

        await handler.Handle(new ApproveWorkspaceAccessRequestCommand(request.Id));

        // The decision, the activation, the membership and its Member role all land together or not at all.
        Assert.Equal(1, _unitOfWork.SaveChangesCallCount);
    }

    [Fact]
    public async Task ConcurrentReviewConflict_ReturnsAlreadyReviewed()
    {
        var (request, _) = Seed(UserStatus.Pending);
        _unitOfWork.ExceptionToThrow = FakeUnitOfWork.WorkspaceAccessRequestConcurrencyConflict();
        var handler = CreateHandler();

        var result = await handler.Handle(new ApproveWorkspaceAccessRequestCommand(request.Id));

        Assert.True(result.IsFailure);
        Assert.Same(WorkspaceAccessRequestErrors.AlreadyReviewed, result.Error);
        Assert.Equal(SmartProperty.Common.Results.ErrorType.Conflict, result.Error!.Type);
        Assert.Equal(1, _unitOfWork.SaveChangesCallCount);
    }

    [Fact]
    public async Task MembershipCollision_WhenAnotherApprovalWon_ReturnsAlreadyReviewed()
    {
        var (request, user) = Seed(UserStatus.Pending);
        var winningReviewerId = Guid.NewGuid();
        var winningReviewedAt = ReviewedAt.AddMinutes(-1);

        _unitOfWork.ExceptionToThrow = FakeUnitOfWork.WorkspaceMembershipUniqueConstraintViolation();
        _unitOfWork.DiscardTrackedChangesAction = () =>
        {
            _memberships.RollBackAdded();
            _roles.RollBackAdded();
            _permissions.RollBackAdded();

            var persistedUser = new User(user.Id, user.Email, user.FirstName, user.LastName, RequestedAt);
            persistedUser.Activate(winningReviewedAt);
            _users.Seed(persistedUser);

            var persistedRequest = new WorkspaceAccessRequest(
                request.Id,
                user.Id,
                _workspaceId,
                RequestedAt);
            persistedRequest.Approve(winningReviewerId, winningReviewedAt);
            _accessRequests.Seed(persistedRequest);
        };
        var handler = CreateHandler();

        var result = await handler.Handle(new ApproveWorkspaceAccessRequestCommand(request.Id));

        Assert.True(result.IsFailure);
        Assert.Same(WorkspaceAccessRequestErrors.AlreadyReviewed, result.Error);
        Assert.Equal(SmartProperty.Common.Results.ErrorType.Conflict, result.Error!.Type);
        Assert.Equal(1, _unitOfWork.SaveChangesCallCount);
        Assert.Equal(1, _unitOfWork.DiscardTrackedChangesCallCount);
        Assert.Equal(2, _coordinator.ExecuteCount);
    }

    [Fact]
    public async Task MembershipCollision_WhenRequestIsPendingAndMembershipNowExists_CompletesApproval()
    {
        var (request, user) = Seed(UserStatus.Pending);
        var persistedMembership = new WorkspaceMembership(
            Guid.NewGuid(),
            user.Id,
            _workspaceId,
            ReviewedAt.AddSeconds(-1));
        WorkspaceAccessRequest? persistedRequest = null;
        User? persistedUser = null;

        _unitOfWork.ExceptionToThrow = FakeUnitOfWork.WorkspaceMembershipUniqueConstraintViolation();
        _unitOfWork.DiscardTrackedChangesAction = () =>
        {
            _memberships.RollBackAdded();
            _roles.RollBackAdded();
            _permissions.RollBackAdded();

            persistedUser = new User(user.Id, user.Email, user.FirstName, user.LastName, RequestedAt);
            _users.Seed(persistedUser);

            persistedRequest = new WorkspaceAccessRequest(
                request.Id,
                user.Id,
                _workspaceId,
                RequestedAt);
            _accessRequests.Seed(persistedRequest);
            _memberships.Seed(persistedMembership);

            // The next save represents the retry after the failed transaction was rolled back and discarded.
            _unitOfWork.ExceptionToThrow = null;
        };
        var handler = CreateHandler();

        var result = await handler.Handle(new ApproveWorkspaceAccessRequestCommand(request.Id));

        Assert.True(result.IsSuccess);
        Assert.Equal(persistedMembership.Id, result.Value.MembershipId);
        Assert.Equal(WorkspaceAccessRequestStatus.Approved, persistedRequest!.Status);
        Assert.Equal(_reviewerUserId, persistedRequest.ReviewedByUserId);
        Assert.Equal(ReviewedAt, persistedRequest.ReviewedAt);
        Assert.Equal(UserStatus.Active, persistedUser!.Status);
        Assert.Empty(_memberships.Added);

        // The retry attaches the Member role to the membership the other writer created.
        var assignment = Assert.Single(_roles.AddedMembershipAssignments);
        Assert.Equal(persistedMembership.Id, assignment.WorkspaceMembershipId);
        Assert.Equal(2, _unitOfWork.SaveChangesCallCount);
        Assert.Equal(1, _unitOfWork.DiscardTrackedChangesCallCount);
        Assert.Equal(2, _coordinator.ExecuteCount);
    }

    [Fact]
    public async Task MembershipCollision_WhenAuthoritativeMembershipIsAbsent_IsNotMisclassified()
    {
        var (request, user) = Seed(UserStatus.Pending);
        var collision = FakeUnitOfWork.WorkspaceMembershipUniqueConstraintViolation();

        _unitOfWork.ExceptionToThrow = collision;
        _unitOfWork.DiscardTrackedChangesAction = () =>
        {
            _memberships.RollBackAdded();
            _roles.RollBackAdded();
            _permissions.RollBackAdded();
            _users.Seed(new User(user.Id, user.Email, user.FirstName, user.LastName, RequestedAt));
            _accessRequests.Seed(new WorkspaceAccessRequest(
                request.Id,
                user.Id,
                _workspaceId,
                RequestedAt));
        };
        var handler = CreateHandler();

        var thrown = await Assert.ThrowsAsync<UniqueConstraintViolationException>(
            () => handler.Handle(new ApproveWorkspaceAccessRequestCommand(request.Id)));

        Assert.Same(collision, thrown);
        Assert.Equal(1, _unitOfWork.SaveChangesCallCount);
        Assert.Equal(1, _unitOfWork.DiscardTrackedChangesCallCount);
        Assert.Equal(2, _coordinator.ExecuteCount);
    }

    [Theory]
    [InlineData(UserStatus.Suspended)]
    [InlineData(UserStatus.Deactivated)]
    public async Task MembershipCollision_WhenUserBecomesUnavailableDuringRecovery_DoesNotReactivate(
        UserStatus unavailableStatus)
    {
        var (request, user) = Seed(UserStatus.Pending);
        WorkspaceAccessRequest? persistedRequest = null;
        User? persistedUser = null;

        _unitOfWork.ExceptionToThrow = FakeUnitOfWork.WorkspaceMembershipUniqueConstraintViolation();
        _unitOfWork.DiscardTrackedChangesAction = () =>
        {
            _memberships.RollBackAdded();
            _roles.RollBackAdded();
            _permissions.RollBackAdded();

            persistedUser = new User(user.Id, user.Email, user.FirstName, user.LastName, RequestedAt);

            if (unavailableStatus == UserStatus.Suspended)
            {
                persistedUser.Suspend(ReviewedAt.AddSeconds(-1));
            }
            else
            {
                persistedUser.Deactivate(ReviewedAt.AddSeconds(-1));
            }

            _users.Seed(persistedUser);

            persistedRequest = new WorkspaceAccessRequest(
                request.Id,
                user.Id,
                _workspaceId,
                RequestedAt);
            _accessRequests.Seed(persistedRequest);
            _memberships.Seed(new WorkspaceMembership(
                Guid.NewGuid(),
                user.Id,
                _workspaceId,
                ReviewedAt.AddSeconds(-1)));
        };
        var handler = CreateHandler();

        var result = await handler.Handle(new ApproveWorkspaceAccessRequestCommand(request.Id));

        Assert.True(result.IsFailure);
        Assert.Same(WorkspaceAccessRequestErrors.TargetAccountUnavailable, result.Error);
        Assert.Equal(unavailableStatus, persistedUser!.Status);
        Assert.Equal(WorkspaceAccessRequestStatus.Pending, persistedRequest!.Status);
        Assert.Null(persistedRequest.ReviewedByUserId);
        Assert.Null(persistedRequest.ReviewedAt);
        Assert.Empty(_memberships.Added);
        Assert.Equal(1, _unitOfWork.SaveChangesCallCount);
        Assert.Equal(1, _unitOfWork.DiscardTrackedChangesCallCount);
        Assert.Equal(2, _coordinator.ExecuteCount);
    }

    [Fact]
    public async Task MembershipCollision_WhenRequestDisappearsDuringRecovery_ReturnsNotFound()
    {
        var (request, user) = Seed(UserStatus.Pending);
        User? persistedUser = null;

        _unitOfWork.ExceptionToThrow = FakeUnitOfWork.WorkspaceMembershipUniqueConstraintViolation();
        _unitOfWork.DiscardTrackedChangesAction = () =>
        {
            _memberships.RollBackAdded();
            _roles.RollBackAdded();
            _permissions.RollBackAdded();
            _accessRequests.Remove(request.Id);

            persistedUser = new User(user.Id, user.Email, user.FirstName, user.LastName, RequestedAt);
            _users.Seed(persistedUser);
        };
        var handler = CreateHandler();

        var result = await handler.Handle(new ApproveWorkspaceAccessRequestCommand(request.Id));

        Assert.True(result.IsFailure);
        Assert.Same(WorkspaceAccessRequestErrors.NotFound, result.Error);
        Assert.NotSame(WorkspaceAccessRequestErrors.AlreadyReviewed, result.Error);
        Assert.Equal(UserStatus.Pending, persistedUser!.Status);
        Assert.Empty(_memberships.Added);
        Assert.Equal(1, _unitOfWork.SaveChangesCallCount);
        Assert.Equal(1, _unitOfWork.DiscardTrackedChangesCallCount);
        Assert.Equal(2, _coordinator.ExecuteCount);
        Assert.Equal(2, _accessRequests.GetByIdCallCount);
    }

    [Fact]
    public async Task MembershipCollision_WhenAnotherReviewerWinsDuringRetry_ReturnsAlreadyReviewedWithoutThirdSave()
    {
        var (request, user) = Seed(UserStatus.Pending);
        var persistedMembership = new WorkspaceMembership(
            Guid.NewGuid(),
            user.Id,
            _workspaceId,
            ReviewedAt.AddSeconds(-1));

        _unitOfWork.ExceptionToThrow = FakeUnitOfWork.WorkspaceMembershipUniqueConstraintViolation();
        _unitOfWork.DiscardTrackedChangesAction = () =>
        {
            _memberships.RollBackAdded();
            _roles.RollBackAdded();
            _permissions.RollBackAdded();
            _users.Seed(new User(user.Id, user.Email, user.FirstName, user.LastName, RequestedAt));
            _accessRequests.Seed(new WorkspaceAccessRequest(
                request.Id,
                user.Id,
                _workspaceId,
                RequestedAt));
            _memberships.Seed(persistedMembership);

            _unitOfWork.ExceptionToThrow = FakeUnitOfWork.WorkspaceAccessRequestConcurrencyConflict();
        };
        var handler = CreateHandler();

        var result = await handler.Handle(new ApproveWorkspaceAccessRequestCommand(request.Id));

        Assert.True(result.IsFailure);
        Assert.Same(WorkspaceAccessRequestErrors.AlreadyReviewed, result.Error);
        Assert.Equal(SmartProperty.Common.Results.ErrorType.Conflict, result.Error!.Type);
        Assert.Equal(2, _unitOfWork.SaveChangesCallCount);
        Assert.Equal(1, _unitOfWork.DiscardTrackedChangesCallCount);
        Assert.Equal(2, _coordinator.ExecuteCount);
        Assert.Empty(_memberships.Added);
    }

    [Fact]
    public async Task Approval_CreatesTheMemberRoleAttachesWorkspaceReadAndAssignsItToTheMembership()
    {
        var (request, _) = Seed(UserStatus.Pending);
        var handler = CreateHandler();

        var result = await handler.Handle(new ApproveWorkspaceAccessRequestCommand(request.Id));

        Assert.True(result.IsSuccess);

        var role = Assert.Single(_roles.AddedRoles);
        Assert.Equal(WorkspaceRoles.MemberRoleName, role.Name);
        Assert.Equal(RoleScope.Workspace, role.Scope);
        Assert.Equal(_workspaceId, role.WorkspaceId);

        var permission = Assert.Single(_permissions.Added);
        Assert.Equal(PermissionCodes.WorkspaceRead, permission.Code);
        Assert.Equal(PermissionCodes.WorkspaceReadDescription, permission.Description);

        var rolePermission = Assert.Single(_roles.AddedRolePermissions);
        Assert.Equal(role.Id, rolePermission.RoleId);
        Assert.Equal(permission.Id, rolePermission.PermissionId);

        var assignment = Assert.Single(_roles.AddedMembershipAssignments);
        Assert.Equal(result.Value.MembershipId, assignment.WorkspaceMembershipId);
        Assert.Equal(role.Id, assignment.RoleId);
        Assert.Equal(1, _unitOfWork.SaveChangesCallCount);
    }

    [Fact]
    public async Task Approval_ReusesAnExistingMemberRoleAndPermission()
    {
        var (request, _) = Seed(UserStatus.Active);
        var (role, permission) = SeedMemberRole(_workspaceId, withPermission: true);
        var handler = CreateHandler();

        var result = await handler.Handle(new ApproveWorkspaceAccessRequestCommand(request.Id));

        Assert.True(result.IsSuccess);
        Assert.Empty(_roles.AddedRoles);
        Assert.Empty(_permissions.Added);
        Assert.Empty(_roles.AddedRolePermissions);

        var assignment = Assert.Single(_roles.AddedMembershipAssignments);
        Assert.Equal(role.Id, assignment.RoleId);
        Assert.Equal(result.Value.MembershipId, assignment.WorkspaceMembershipId);
        Assert.Single(_roles.RolePermissions, rp => rp.RoleId == role.Id && rp.PermissionId == permission.Id);
    }

    [Fact]
    public async Task Approval_AttachesWorkspaceReadToAnExistingMemberRoleThatLacksIt()
    {
        var (request, _) = Seed(UserStatus.Active);
        var (role, permission) = SeedMemberRole(_workspaceId, withPermission: false);
        var handler = CreateHandler();

        var result = await handler.Handle(new ApproveWorkspaceAccessRequestCommand(request.Id));

        Assert.True(result.IsSuccess);
        Assert.Empty(_roles.AddedRoles);
        Assert.Empty(_permissions.Added);

        var rolePermission = Assert.Single(_roles.AddedRolePermissions);
        Assert.Equal(role.Id, rolePermission.RoleId);
        Assert.Equal(permission.Id, rolePermission.PermissionId);
    }

    [Fact]
    public async Task Approval_DoesNotDuplicateAnExistingMembershipRole()
    {
        var (request, user) = Seed(UserStatus.Active);
        var (role, _) = SeedMemberRole(_workspaceId, withPermission: true);
        var existing = new WorkspaceMembership(Guid.NewGuid(), user.Id, _workspaceId, RequestedAt);
        _memberships.Seed(existing);
        _roles.Seed(new WorkspaceMembershipRole(existing.Id, role.Id));
        var handler = CreateHandler();

        var result = await handler.Handle(new ApproveWorkspaceAccessRequestCommand(request.Id));

        Assert.True(result.IsSuccess);
        Assert.Empty(_roles.AddedMembershipAssignments);
        Assert.Single(_roles.MembershipAssignments);
    }

    [Fact]
    public async Task Approval_NeverReusesAnotherWorkspacesMemberRole()
    {
        var (request, _) = Seed(UserStatus.Active);
        var (otherWorkspaceRole, _) = SeedMemberRole(Guid.NewGuid(), withPermission: true);
        var handler = CreateHandler();

        var result = await handler.Handle(new ApproveWorkspaceAccessRequestCommand(request.Id));

        Assert.True(result.IsSuccess);

        var created = Assert.Single(_roles.AddedRoles);
        Assert.NotEqual(otherWorkspaceRole.Id, created.Id);
        Assert.Equal(_workspaceId, created.WorkspaceId);
        Assert.Equal(created.Id, Assert.Single(_roles.AddedMembershipAssignments).RoleId);
    }

    [Theory]
    [InlineData(UserStatus.Suspended)]
    [InlineData(UserStatus.Deactivated)]
    public async Task DisabledUser_GetsNoMemberRole(UserStatus status)
    {
        var (request, _) = Seed(status);
        var handler = CreateHandler();

        var result = await handler.Handle(new ApproveWorkspaceAccessRequestCommand(request.Id));

        Assert.True(result.IsFailure);
        Assert.Empty(_roles.AddedRoles);
        Assert.Empty(_permissions.Added);
        Assert.Empty(_roles.AddedRolePermissions);
        Assert.Empty(_roles.AddedMembershipAssignments);
    }

    [Fact]
    public async Task WorkspaceRoleNameCollision_WithNoMembership_CreatesTheMembershipAndAttachesTheWinnersRole()
    {
        var (request, user) = Seed(UserStatus.Pending);
        Role? winnerRole = null;
        WorkspaceAccessRequest? persistedRequest = null;

        _unitOfWork.ExceptionToThrow = FakeUnitOfWork.WorkspaceRoleNameUniqueConstraintViolation();
        _unitOfWork.DiscardTrackedChangesAction = () =>
        {
            (persistedRequest, _) = ReloadAfterRollBack(request, user);

            // The other approval committed the Member role, workspace.read, and their link.
            (winnerRole, _) = SeedMemberRole(_workspaceId, withPermission: true);
            _unitOfWork.ExceptionToThrow = null;
        };
        var handler = CreateHandler();

        var result = await handler.Handle(new ApproveWorkspaceAccessRequestCommand(request.Id));

        Assert.True(result.IsSuccess);
        Assert.Equal(WorkspaceAccessRequestStatus.Approved, persistedRequest!.Status);

        var membership = Assert.Single(_memberships.Added);
        Assert.Equal(membership.Id, result.Value.MembershipId);

        // Reused, not recreated.
        Assert.Empty(_roles.AddedRoles);
        Assert.Empty(_permissions.Added);
        Assert.Empty(_roles.AddedRolePermissions);

        var assignment = Assert.Single(_roles.AddedMembershipAssignments);
        Assert.Equal(membership.Id, assignment.WorkspaceMembershipId);
        Assert.Equal(winnerRole!.Id, assignment.RoleId);
        Assert.Equal(2, _unitOfWork.SaveChangesCallCount);
        Assert.Equal(1, _unitOfWork.DiscardTrackedChangesCallCount);
        Assert.Equal(2, _coordinator.ExecuteCount);
    }

    [Fact]
    public async Task PermissionCodeCollision_WithNoMembership_CreatesTheMembershipAndAttachesMember()
    {
        var (request, user) = Seed(UserStatus.Pending);
        Permission? winnerPermission = null;

        _unitOfWork.ExceptionToThrow = FakeUnitOfWork.PermissionCodeUniqueConstraintViolation();
        _unitOfWork.DiscardTrackedChangesAction = () =>
        {
            ReloadAfterRollBack(request, user);

            // The other approval committed only workspace.read; the Member role is still missing.
            winnerPermission = new Permission(
                Guid.NewGuid(),
                PermissionCodes.WorkspaceRead,
                PermissionCodes.WorkspaceReadDescription);
            _permissions.Seed(winnerPermission);
            _unitOfWork.ExceptionToThrow = null;
        };
        var handler = CreateHandler();

        var result = await handler.Handle(new ApproveWorkspaceAccessRequestCommand(request.Id));

        Assert.True(result.IsSuccess);

        var membership = Assert.Single(_memberships.Added);
        Assert.Equal(membership.Id, result.Value.MembershipId);
        Assert.Empty(_permissions.Added);

        var role = Assert.Single(_roles.AddedRoles);
        var rolePermission = Assert.Single(_roles.AddedRolePermissions);
        Assert.Equal(role.Id, rolePermission.RoleId);
        Assert.Equal(winnerPermission!.Id, rolePermission.PermissionId);

        var assignment = Assert.Single(_roles.AddedMembershipAssignments);
        Assert.Equal(membership.Id, assignment.WorkspaceMembershipId);
        Assert.Equal(role.Id, assignment.RoleId);
        Assert.Equal(2, _unitOfWork.SaveChangesCallCount);
        Assert.Equal(1, _unitOfWork.DiscardTrackedChangesCallCount);
        Assert.Equal(2, _coordinator.ExecuteCount);
    }

    [Fact]
    public async Task MembershipCollision_AlsoAttachesTheMemberRoleDuringTheRetry()
    {
        var (request, user) = Seed(UserStatus.Pending);
        var persistedMembership = new WorkspaceMembership(
            Guid.NewGuid(),
            user.Id,
            _workspaceId,
            ReviewedAt.AddSeconds(-1));

        _unitOfWork.ExceptionToThrow = FakeUnitOfWork.WorkspaceMembershipUniqueConstraintViolation();
        _unitOfWork.DiscardTrackedChangesAction = () =>
        {
            ReloadAfterRollBack(request, user);
            _memberships.Seed(persistedMembership);
            _unitOfWork.ExceptionToThrow = null;
        };
        var handler = CreateHandler();

        var result = await handler.Handle(new ApproveWorkspaceAccessRequestCommand(request.Id));

        Assert.True(result.IsSuccess);
        Assert.Equal(persistedMembership.Id, result.Value.MembershipId);
        Assert.Empty(_memberships.Added);

        var role = Assert.Single(_roles.AddedRoles);
        Assert.Equal(WorkspaceRoles.MemberRoleName, role.Name);

        var assignment = Assert.Single(_roles.AddedMembershipAssignments);
        Assert.Equal(persistedMembership.Id, assignment.WorkspaceMembershipId);
        Assert.Equal(role.Id, assignment.RoleId);
        Assert.Equal(2, _unitOfWork.SaveChangesCallCount);
    }

    [Fact]
    public async Task WorkspaceRoleNameCollision_ThatFailsAgainOnRetry_Propagates()
    {
        var (request, user) = Seed(UserStatus.Pending);
        var secondCollision = FakeUnitOfWork.WorkspaceRoleNameUniqueConstraintViolation();

        _unitOfWork.ExceptionToThrow = FakeUnitOfWork.WorkspaceRoleNameUniqueConstraintViolation();
        _unitOfWork.DiscardTrackedChangesAction = () =>
        {
            ReloadAfterRollBack(request, user);

            // The collided Member role now exists, so the recovery is confirmed and the retry actually runs.
            SeedMemberRole(_workspaceId, withPermission: true);
            _unitOfWork.ExceptionToThrow = secondCollision;
        };
        var handler = CreateHandler();

        var thrown = await Assert.ThrowsAsync<UniqueConstraintViolationException>(
            () => handler.Handle(new ApproveWorkspaceAccessRequestCommand(request.Id)));

        // One attempt only: no third save and no second discard.
        Assert.Same(secondCollision, thrown);
        Assert.Equal(2, _unitOfWork.SaveChangesCallCount);
        Assert.Equal(1, _unitOfWork.DiscardTrackedChangesCallCount);
        Assert.Equal(2, _coordinator.ExecuteCount);
    }

    [Fact]
    public async Task AlreadyApprovedRequest_CannotBeApprovedAgain()
    {
        var (request, _) = Seed(UserStatus.Active);
        var handler = CreateHandler();

        var first = await handler.Handle(new ApproveWorkspaceAccessRequestCommand(request.Id));
        var second = await handler.Handle(new ApproveWorkspaceAccessRequestCommand(request.Id));

        Assert.True(first.IsSuccess);
        Assert.True(second.IsFailure);
        Assert.Same(WorkspaceAccessRequestErrors.AlreadyReviewed, second.Error);

        // No second membership, and the first decision stands.
        Assert.Single(_memberships.Added);
        Assert.Equal(1, _unitOfWork.SaveChangesCallCount);
        Assert.Equal(_reviewerUserId, request.ReviewedByUserId);
    }

    [Fact]
    public async Task RejectedRequest_CannotBeApproved()
    {
        var (request, user) = Seed(UserStatus.Pending);
        var otherReviewerId = Guid.NewGuid();
        request.Reject(otherReviewerId, RequestedAt);
        var handler = CreateHandler();

        var result = await handler.Handle(new ApproveWorkspaceAccessRequestCommand(request.Id));

        Assert.True(result.IsFailure);
        Assert.Same(WorkspaceAccessRequestErrors.AlreadyReviewed, result.Error);
        Assert.Equal(WorkspaceAccessRequestStatus.Rejected, request.Status);
        Assert.Equal(otherReviewerId, request.ReviewedByUserId);
        Assert.Equal(UserStatus.Pending, user.Status);
        Assert.Empty(_memberships.Added);
    }

    [Fact]
    public async Task EmptyRequestId_IsTreatedAsNotFoundRatherThanReachingTheRepository()
    {
        var handler = CreateHandler();

        var result = await handler.Handle(new ApproveWorkspaceAccessRequestCommand(Guid.Empty));

        Assert.True(result.IsFailure);
        Assert.Same(WorkspaceAccessRequestErrors.NotFound, result.Error);
        Assert.Equal(0, _accessRequests.GetByIdCallCount);
    }

    [Fact]
    public async Task MissingMemberRole_IsCreatedWithExactlyWorkspaceRead()
    {
        var (request, _) = Seed(UserStatus.Pending);
        var handler = CreateHandler();

        var result = await handler.Handle(new ApproveWorkspaceAccessRequestCommand(request.Id));

        Assert.True(result.IsSuccess);

        var role = Assert.Single(_roles.AddedRoles);
        var permission = Assert.Single(_permissions.Added);

        // Nothing but workspace.read, and nothing is assigned beyond the one Member assignment.
        var link = Assert.Single(_roles.RolePermissions);
        Assert.Equal(role.Id, link.RoleId);
        Assert.Equal(permission.Id, link.PermissionId);
        Assert.Equal(PermissionCodes.WorkspaceRead, permission.Code);
        Assert.Single(_roles.MembershipAssignments);
        Assert.Empty(_roles.UserAssignments);
    }

    [Fact]
    public async Task ZeroPermissionMemberWithNoAssignments_IsInitializedWithExactlyWorkspaceRead()
    {
        var (request, _) = Seed(UserStatus.Pending);
        var role = await SeedMemberRoleWithPermissionsAsync(_workspaceId);
        var workspaceRead = await GetOrSeedPermissionAsync(PermissionCodes.WorkspaceRead);
        var handler = CreateHandler();

        var result = await handler.Handle(new ApproveWorkspaceAccessRequestCommand(request.Id));

        Assert.True(result.IsSuccess);
        Assert.Empty(_roles.AddedRoles);
        Assert.Empty(_permissions.Added);

        var link = Assert.Single(_roles.AddedRolePermissions);
        Assert.Equal(role.Id, link.RoleId);
        Assert.Equal(workspaceRead.Id, link.PermissionId);
        Assert.Single(_roles.RolePermissions, rolePermission => rolePermission.RoleId == role.Id);

        var assignment = Assert.Single(_roles.AddedMembershipAssignments);
        Assert.Equal(role.Id, assignment.RoleId);
        Assert.Equal(result.Value.MembershipId, assignment.WorkspaceMembershipId);
        Assert.Equal(1, _unitOfWork.SaveChangesCallCount);
    }

    [Fact]
    public async Task ZeroPermissionMemberAssignedToAnotherMembership_FailsClosedWithoutChangingAnything()
    {
        var (request, user) = Seed(UserStatus.Pending);
        var role = await SeedMemberRoleWithPermissionsAsync(_workspaceId);
        _roles.Seed(new WorkspaceMembershipRole(Guid.NewGuid(), role.Id));
        var handler = CreateHandler();

        var result = await handler.Handle(new ApproveWorkspaceAccessRequestCommand(request.Id));

        AssertMemberRoleMisconfigured(result, request, user, UserStatus.Pending);
        Assert.Empty(_roles.RolePermissions);
    }

    [Fact]
    public async Task ZeroPermissionMemberWithSeveralAssignments_FailsClosedWithoutChangingAnything()
    {
        var (request, user) = Seed(UserStatus.Pending);
        var role = await SeedMemberRoleWithPermissionsAsync(_workspaceId);
        _roles.Seed(new WorkspaceMembershipRole(Guid.NewGuid(), role.Id));
        _roles.Seed(new WorkspaceMembershipRole(Guid.NewGuid(), role.Id));
        _roles.Seed(new WorkspaceMembershipRole(Guid.NewGuid(), role.Id));
        var handler = CreateHandler();

        var result = await handler.Handle(new ApproveWorkspaceAccessRequestCommand(request.Id));

        AssertMemberRoleMisconfigured(result, request, user, UserStatus.Pending);
        Assert.Empty(_roles.RolePermissions);
        Assert.Equal(3, _roles.MembershipAssignments.Count);
    }

    [Fact]
    public async Task ZeroPermissionMemberAlreadyAssignedToThisMembership_FailsClosedWithoutChangingAnything()
    {
        var (request, user) = Seed(UserStatus.Active);
        var existing = new WorkspaceMembership(Guid.NewGuid(), user.Id, _workspaceId, RequestedAt);
        _memberships.Seed(existing);
        var role = await SeedMemberRoleWithPermissionsAsync(_workspaceId);
        _roles.Seed(new WorkspaceMembershipRole(existing.Id, role.Id));
        var handler = CreateHandler();

        var result = await handler.Handle(new ApproveWorkspaceAccessRequestCommand(request.Id));

        AssertMemberRoleMisconfigured(result, request, user, UserStatus.Active);
        Assert.Empty(_roles.RolePermissions);
        Assert.Single(_roles.MembershipAssignments);
    }

    [Theory]
    [InlineData(true, PermissionCodes.PropertyRead)]
    [InlineData(true, PermissionCodes.WorkspaceAccessRequestsReview)]
    [InlineData(false, PermissionCodes.PropertyRead)]
    [InlineData(false, PermissionCodes.WorkspaceAccessRequestsReview)]
    public async Task MemberWithAnyOtherPermission_FailsClosedWithoutChangingAnything(
        bool alsoCarriesWorkspaceRead,
        string otherPermissionCode)
    {
        var (request, user) = Seed(UserStatus.Pending);
        var codes = alsoCarriesWorkspaceRead
            ? new[] { PermissionCodes.WorkspaceRead, otherPermissionCode }
            : new[] { otherPermissionCode };
        var role = await SeedMemberRoleWithPermissionsAsync(_workspaceId, codes);
        var permissionCountBefore = _roles.RolePermissions.Count(link => link.RoleId == role.Id);
        var handler = CreateHandler();

        var result = await handler.Handle(new ApproveWorkspaceAccessRequestCommand(request.Id));

        AssertMemberRoleMisconfigured(result, request, user, UserStatus.Pending);

        // The role is reported, never repaired: its permissions are exactly what they were.
        Assert.Equal(permissionCountBefore, _roles.RolePermissions.Count(link => link.RoleId == role.Id));
        Assert.Empty(_roles.MembershipAssignments);
    }

    [Fact]
    public async Task MisconfiguredMember_DoesNotRevokeOrDisturbItsExistingHolders()
    {
        var (request, user) = Seed(UserStatus.Active);
        var role = await SeedMemberRoleWithPermissionsAsync(_workspaceId, PermissionCodes.PropertyRead);
        var otherHolder = new WorkspaceMembershipRole(Guid.NewGuid(), role.Id);
        _roles.Seed(otherHolder);
        var handler = CreateHandler();

        var result = await handler.Handle(new ApproveWorkspaceAccessRequestCommand(request.Id));

        AssertMemberRoleMisconfigured(result, request, user, UserStatus.Active);
        Assert.Same(otherHolder, Assert.Single(_roles.MembershipAssignments));
    }

    [Fact]
    public async Task Approval_PreservesRolesTheMembershipHoldsIndependently()
    {
        var (request, user) = Seed(UserStatus.Active);
        var existing = new WorkspaceMembership(Guid.NewGuid(), user.Id, _workspaceId, RequestedAt);
        _memberships.Seed(existing);

        var editor = new Role(Guid.NewGuid(), "Editor", RoleScope.Workspace, _workspaceId, RequestedAt);
        _roles.Seed(editor);
        var propertyRead = await GetOrSeedPermissionAsync(PermissionCodes.PropertyRead);
        _roles.Seed(new RolePermission(editor.Id, propertyRead.Id));
        var editorAssignment = new WorkspaceMembershipRole(existing.Id, editor.Id);
        _roles.Seed(editorAssignment);
        var handler = CreateHandler();

        var result = await handler.Handle(new ApproveWorkspaceAccessRequestCommand(request.Id));

        Assert.True(result.IsSuccess);

        // The independent role, its permission and its assignment are untouched; only Member is added.
        Assert.Contains(editorAssignment, _roles.MembershipAssignments);
        Assert.Single(_roles.RolePermissions, link => link.RoleId == editor.Id && link.PermissionId == propertyRead.Id);

        var added = Assert.Single(_roles.AddedMembershipAssignments);
        Assert.NotEqual(editor.Id, added.RoleId);
        Assert.Equal(existing.Id, added.WorkspaceMembershipId);
        Assert.Empty(_roles.AddedUserAssignments);
    }

    [Fact]
    public async Task AnotherWorkspacesMisconfiguredMember_DoesNotBlockOrChangeThisApproval()
    {
        var (request, _) = Seed(UserStatus.Active);
        var otherWorkspaceId = Guid.NewGuid();
        var otherRole = await SeedMemberRoleWithPermissionsAsync(otherWorkspaceId, PermissionCodes.PropertyRead);
        var otherHolder = new WorkspaceMembershipRole(Guid.NewGuid(), otherRole.Id);
        _roles.Seed(otherHolder);
        var handler = CreateHandler();

        var result = await handler.Handle(new ApproveWorkspaceAccessRequestCommand(request.Id));

        Assert.True(result.IsSuccess);

        var created = Assert.Single(_roles.AddedRoles);
        Assert.Equal(_workspaceId, created.WorkspaceId);
        Assert.Single(_roles.RolePermissions, link => link.RoleId == otherRole.Id);
        Assert.Contains(otherHolder, _roles.MembershipAssignments);
        Assert.DoesNotContain(_roles.AddedMembershipAssignments, assignment => assignment.RoleId == otherRole.Id);
    }

    [Fact]
    public async Task PlatformRoleNamedMember_IsNeverReusedAndNoPlatformPermissionIsGranted()
    {
        var (request, _) = Seed(UserStatus.Active);
        var platformRole = new Role(
            Guid.NewGuid(),
            WorkspaceRoles.MemberRoleName,
            RoleScope.Platform,
            null,
            RequestedAt);
        _roles.Seed(platformRole);
        var platformPermission = await GetOrSeedPermissionAsync(PermissionCodes.WorkspaceAccessRequestsReview);
        _roles.Seed(new RolePermission(platformRole.Id, platformPermission.Id));
        var handler = CreateHandler();

        var result = await handler.Handle(new ApproveWorkspaceAccessRequestCommand(request.Id));

        Assert.True(result.IsSuccess);

        var created = Assert.Single(_roles.AddedRoles);
        Assert.NotEqual(platformRole.Id, created.Id);
        Assert.Equal(RoleScope.Workspace, created.Scope);

        // Only workspace.read is ever linked, and no platform assignment is written.
        var workspaceRead = await _permissions.GetByCodeAsync(PermissionCodes.WorkspaceRead);
        Assert.All(_roles.AddedRolePermissions, link => Assert.Equal(workspaceRead!.Id, link.PermissionId));
        Assert.Single(_roles.RolePermissions, link => link.RoleId == platformRole.Id);
        Assert.Empty(_roles.AddedUserAssignments);
        Assert.Empty(_roles.UserAssignments);
        Assert.Equal(created.Id, Assert.Single(_roles.AddedMembershipAssignments).RoleId);
    }

    [Fact]
    public async Task RolePermissionKeyCollision_WhenWinnerLinkedWorkspaceRead_ReusesItAndAssigns()
    {
        var (request, user) = Seed(UserStatus.Pending);
        var role = await SeedMemberRoleWithPermissionsAsync(_workspaceId);
        var workspaceRead = await GetOrSeedPermissionAsync(PermissionCodes.WorkspaceRead);

        _unitOfWork.ExceptionToThrow = FakeUnitOfWork.RolePermissionKeyUniqueConstraintViolation();
        _unitOfWork.DiscardTrackedChangesAction = () =>
        {
            ReloadAfterRollBack(request, user);

            // The other approval committed the link and the assignment of its own membership.
            _roles.Seed(new RolePermission(role.Id, workspaceRead.Id));
            _roles.Seed(new WorkspaceMembershipRole(Guid.NewGuid(), role.Id));
            _unitOfWork.ExceptionToThrow = null;
        };
        var handler = CreateHandler();

        var result = await handler.Handle(new ApproveWorkspaceAccessRequestCommand(request.Id));

        Assert.True(result.IsSuccess);
        Assert.Empty(_roles.AddedRolePermissions);
        Assert.Single(_roles.RolePermissions, link => link.RoleId == role.Id);

        var membership = Assert.Single(_memberships.Added);
        var assignment = Assert.Single(_roles.AddedMembershipAssignments);
        Assert.Equal(membership.Id, assignment.WorkspaceMembershipId);
        Assert.Equal(role.Id, assignment.RoleId);
        Assert.Equal(2, _unitOfWork.SaveChangesCallCount);
        Assert.Equal(1, _unitOfWork.DiscardTrackedChangesCallCount);
        Assert.Equal(2, _coordinator.ExecuteCount);
    }

    [Fact]
    public async Task WorkspaceMembershipRoleKeyCollision_WhenWinnerAssignedIt_AddsNothingMore()
    {
        var (request, user) = Seed(UserStatus.Pending);
        var existing = new WorkspaceMembership(Guid.NewGuid(), user.Id, _workspaceId, RequestedAt);
        _memberships.Seed(existing);
        var (role, _) = SeedMemberRole(_workspaceId, withPermission: true);

        _unitOfWork.ExceptionToThrow = FakeUnitOfWork.WorkspaceMembershipRoleKeyUniqueConstraintViolation();
        _unitOfWork.DiscardTrackedChangesAction = () =>
        {
            ReloadAfterRollBack(request, user);
            _roles.Seed(new WorkspaceMembershipRole(existing.Id, role.Id));
            _unitOfWork.ExceptionToThrow = null;
        };
        var handler = CreateHandler();

        var result = await handler.Handle(new ApproveWorkspaceAccessRequestCommand(request.Id));

        Assert.True(result.IsSuccess);
        Assert.Equal(existing.Id, result.Value.MembershipId);
        Assert.Empty(_memberships.Added);
        Assert.Empty(_roles.AddedMembershipAssignments);
        Assert.Single(_roles.MembershipAssignments);
        Assert.Equal(2, _unitOfWork.SaveChangesCallCount);
        Assert.Equal(1, _unitOfWork.DiscardTrackedChangesCallCount);
        Assert.Equal(2, _coordinator.ExecuteCount);
    }

    [Fact]
    public async Task RolePermissionKeyCollision_WithoutThePersistedLink_RethrowsTheOriginalException()
    {
        var (request, user) = Seed(UserStatus.Pending);
        await SeedMemberRoleWithPermissionsAsync(_workspaceId);
        await GetOrSeedPermissionAsync(PermissionCodes.WorkspaceRead);
        var collision = FakeUnitOfWork.RolePermissionKeyUniqueConstraintViolation();

        _unitOfWork.ExceptionToThrow = collision;

        // The Member role and workspace.read both exist after the reload, but the collided link does not: the
        // evidence is the exact row, not a valid-looking role.
        _unitOfWork.DiscardTrackedChangesAction = () => ReloadAfterRollBack(request, user);
        var handler = CreateHandler();

        var thrown = await Assert.ThrowsAsync<UniqueConstraintViolationException>(
            () => handler.Handle(new ApproveWorkspaceAccessRequestCommand(request.Id)));

        Assert.Same(collision, thrown);
        Assert.Equal(1, _unitOfWork.SaveChangesCallCount);
        Assert.Equal(1, _unitOfWork.DiscardTrackedChangesCallCount);
        Assert.Equal(2, _coordinator.ExecuteCount);
        Assert.Empty(_roles.AddedRolePermissions);
        Assert.Empty(_roles.AddedMembershipAssignments);
    }

    [Fact]
    public async Task WorkspaceMembershipRoleKeyCollision_WithoutThePersistedAssignment_RethrowsTheOriginalException()
    {
        var (request, user) = Seed(UserStatus.Pending);
        var existing = new WorkspaceMembership(Guid.NewGuid(), user.Id, _workspaceId, RequestedAt);
        _memberships.Seed(existing);
        SeedMemberRole(_workspaceId, withPermission: true);
        var collision = FakeUnitOfWork.WorkspaceMembershipRoleKeyUniqueConstraintViolation();

        _unitOfWork.ExceptionToThrow = collision;
        _unitOfWork.DiscardTrackedChangesAction = () => ReloadAfterRollBack(request, user);
        var handler = CreateHandler();

        var thrown = await Assert.ThrowsAsync<UniqueConstraintViolationException>(
            () => handler.Handle(new ApproveWorkspaceAccessRequestCommand(request.Id)));

        Assert.Same(collision, thrown);
        Assert.Equal(1, _unitOfWork.SaveChangesCallCount);
        Assert.Equal(1, _unitOfWork.DiscardTrackedChangesCallCount);
        Assert.Equal(2, _coordinator.ExecuteCount);
        Assert.Empty(_roles.AddedMembershipAssignments);
        Assert.Empty(_roles.MembershipAssignments);
    }

    [Fact]
    public async Task WorkspaceMembershipRoleKeyCollision_WithoutAMembership_RethrowsTheOriginalException()
    {
        var (request, user) = Seed(UserStatus.Pending);
        SeedMemberRole(_workspaceId, withPermission: true);
        var collision = FakeUnitOfWork.WorkspaceMembershipRoleKeyUniqueConstraintViolation();

        _unitOfWork.ExceptionToThrow = collision;
        _unitOfWork.DiscardTrackedChangesAction = () => ReloadAfterRollBack(request, user);
        var handler = CreateHandler();

        var thrown = await Assert.ThrowsAsync<UniqueConstraintViolationException>(
            () => handler.Handle(new ApproveWorkspaceAccessRequestCommand(request.Id)));

        Assert.Same(collision, thrown);
        Assert.Equal(1, _unitOfWork.SaveChangesCallCount);
        Assert.Empty(_memberships.Added);
    }

    [Fact]
    public async Task RolePermissionKeyCollision_ThenARoleWithExtraPermissions_FailsClosedWithoutASecondSave()
    {
        var (request, user) = Seed(UserStatus.Pending);
        var role = await SeedMemberRoleWithPermissionsAsync(_workspaceId);
        var workspaceRead = await GetOrSeedPermissionAsync(PermissionCodes.WorkspaceRead);
        var propertyRead = await GetOrSeedPermissionAsync(PermissionCodes.PropertyRead);
        WorkspaceAccessRequest? persistedRequest = null;
        User? persistedUser = null;

        _unitOfWork.ExceptionToThrow = FakeUnitOfWork.RolePermissionKeyUniqueConstraintViolation();
        _unitOfWork.DiscardTrackedChangesAction = () =>
        {
            (persistedRequest, persistedUser) = ReloadAfterRollBack(request, user);

            // The collided link exists, so the recovery is confirmed — but the role now also carries another
            // permission, which the complete revalidation must refuse.
            _roles.Seed(new RolePermission(role.Id, workspaceRead.Id));
            _roles.Seed(new RolePermission(role.Id, propertyRead.Id));
            _unitOfWork.ExceptionToThrow = null;
        };
        var handler = CreateHandler();

        var result = await handler.Handle(new ApproveWorkspaceAccessRequestCommand(request.Id));

        Assert.True(result.IsFailure);
        Assert.Same(WorkspaceAccessRequestErrors.MemberRoleMisconfigured, result.Error);
        Assert.Equal(1, _unitOfWork.SaveChangesCallCount);
        Assert.Equal(1, _unitOfWork.DiscardTrackedChangesCallCount);
        Assert.Equal(2, _coordinator.ExecuteCount);
        Assert.Equal(WorkspaceAccessRequestStatus.Pending, persistedRequest!.Status);
        Assert.Equal(UserStatus.Pending, persistedUser!.Status);
        Assert.Empty(_memberships.Added);
        Assert.Empty(_roles.AddedRolePermissions);
        Assert.Empty(_roles.AddedMembershipAssignments);
    }

    [Fact]
    public async Task WorkspaceMembershipRoleKeyCollision_ThenARoleWithExtraPermissions_FailsClosedWithoutASecondSave()
    {
        var (request, user) = Seed(UserStatus.Pending);
        var existing = new WorkspaceMembership(Guid.NewGuid(), user.Id, _workspaceId, RequestedAt);
        _memberships.Seed(existing);
        var (role, _) = SeedMemberRole(_workspaceId, withPermission: true);
        var propertyRead = await GetOrSeedPermissionAsync(PermissionCodes.PropertyRead);
        WorkspaceAccessRequest? persistedRequest = null;

        _unitOfWork.ExceptionToThrow = FakeUnitOfWork.WorkspaceMembershipRoleKeyUniqueConstraintViolation();
        _unitOfWork.DiscardTrackedChangesAction = () =>
        {
            (persistedRequest, _) = ReloadAfterRollBack(request, user);
            _roles.Seed(new WorkspaceMembershipRole(existing.Id, role.Id));
            _roles.Seed(new RolePermission(role.Id, propertyRead.Id));
            _unitOfWork.ExceptionToThrow = null;
        };
        var handler = CreateHandler();

        var result = await handler.Handle(new ApproveWorkspaceAccessRequestCommand(request.Id));

        Assert.True(result.IsFailure);
        Assert.Same(WorkspaceAccessRequestErrors.MemberRoleMisconfigured, result.Error);
        Assert.Equal(1, _unitOfWork.SaveChangesCallCount);
        Assert.Equal(1, _unitOfWork.DiscardTrackedChangesCallCount);
        Assert.Equal(2, _coordinator.ExecuteCount);
        Assert.Equal(WorkspaceAccessRequestStatus.Pending, persistedRequest!.Status);
        Assert.Empty(_roles.AddedMembershipAssignments);
    }

    [Fact]
    public async Task RoleNameCollision_ThenAnEmptyMemberWithAnAssignment_FailsClosedWithoutASecondSave()
    {
        var (request, user) = Seed(UserStatus.Pending);
        WorkspaceAccessRequest? persistedRequest = null;
        User? persistedUser = null;

        _unitOfWork.ExceptionToThrow = FakeUnitOfWork.WorkspaceRoleNameUniqueConstraintViolation();
        _unitOfWork.DiscardTrackedChangesAction = () =>
        {
            (persistedRequest, persistedUser) = ReloadAfterRollBack(request, user);

            // The collided Member role exists, so the recovery is confirmed — but it has no permissions and is
            // already held, which the complete revalidation must refuse rather than complete.
            var role = SeedMemberRoleWithPermissionsSync(_workspaceId);
            _roles.Seed(new WorkspaceMembershipRole(Guid.NewGuid(), role.Id));

            // A second save would succeed here, so a count of one proves none was attempted.
            _unitOfWork.ExceptionToThrow = null;
        };
        var handler = CreateHandler();

        var result = await handler.Handle(new ApproveWorkspaceAccessRequestCommand(request.Id));

        Assert.True(result.IsFailure);
        Assert.Same(WorkspaceAccessRequestErrors.MemberRoleMisconfigured, result.Error);
        Assert.Equal(1, _unitOfWork.SaveChangesCallCount);
        Assert.Equal(1, _unitOfWork.DiscardTrackedChangesCallCount);
        Assert.Equal(2, _coordinator.ExecuteCount);
        Assert.Equal(WorkspaceAccessRequestStatus.Pending, persistedRequest!.Status);
        Assert.Null(persistedRequest.ReviewedByUserId);
        Assert.Equal(UserStatus.Pending, persistedUser!.Status);
        Assert.Empty(_memberships.Added);
        Assert.Empty(_roles.AddedRoles);
        Assert.Empty(_roles.AddedRolePermissions);
        Assert.Empty(_roles.AddedMembershipAssignments);
        Assert.Empty(_roles.RolePermissions);
    }

    [Fact]
    public async Task RolePermissionKeyCollision_ThatFailsAgainOnRetry_PropagatesAfterOneRecovery()
    {
        var (request, user) = Seed(UserStatus.Pending);
        var role = await SeedMemberRoleWithPermissionsAsync(_workspaceId);
        var workspaceRead = await GetOrSeedPermissionAsync(PermissionCodes.WorkspaceRead);
        var secondCollision = FakeUnitOfWork.RolePermissionKeyUniqueConstraintViolation();

        _unitOfWork.ExceptionToThrow = FakeUnitOfWork.RolePermissionKeyUniqueConstraintViolation();
        _unitOfWork.DiscardTrackedChangesAction = () =>
        {
            ReloadAfterRollBack(request, user);
            _roles.Seed(new RolePermission(role.Id, workspaceRead.Id));
            _unitOfWork.ExceptionToThrow = secondCollision;
        };
        var handler = CreateHandler();

        var thrown = await Assert.ThrowsAsync<UniqueConstraintViolationException>(
            () => handler.Handle(new ApproveWorkspaceAccessRequestCommand(request.Id)));

        // One recovery only: no third save and no second discard.
        Assert.Same(secondCollision, thrown);
        Assert.Equal(2, _unitOfWork.SaveChangesCallCount);
        Assert.Equal(1, _unitOfWork.DiscardTrackedChangesCallCount);
        Assert.Equal(2, _coordinator.ExecuteCount);
    }

    [Fact]
    public async Task WorkspaceMembershipRoleKeyCollision_ThatFailsAgainOnRetry_PropagatesAfterOneRecovery()
    {
        var (request, user) = Seed(UserStatus.Pending);
        var existing = new WorkspaceMembership(Guid.NewGuid(), user.Id, _workspaceId, RequestedAt);
        _memberships.Seed(existing);
        var (role, _) = SeedMemberRole(_workspaceId, withPermission: true);
        var secondCollision = FakeUnitOfWork.WorkspaceMembershipRoleKeyUniqueConstraintViolation();

        _unitOfWork.ExceptionToThrow = FakeUnitOfWork.WorkspaceMembershipRoleKeyUniqueConstraintViolation();
        _unitOfWork.DiscardTrackedChangesAction = () =>
        {
            ReloadAfterRollBack(request, user);
            _roles.Seed(new WorkspaceMembershipRole(existing.Id, role.Id));
            _unitOfWork.ExceptionToThrow = secondCollision;
        };
        var handler = CreateHandler();

        var thrown = await Assert.ThrowsAsync<UniqueConstraintViolationException>(
            () => handler.Handle(new ApproveWorkspaceAccessRequestCommand(request.Id)));

        Assert.Same(secondCollision, thrown);
        Assert.Equal(2, _unitOfWork.SaveChangesCallCount);
        Assert.Equal(1, _unitOfWork.DiscardTrackedChangesCallCount);
        Assert.Equal(2, _coordinator.ExecuteCount);
    }

    // ---- Read-phase races: the policy reads are separate statements, so another approval's commit can land between
    // them. The fake's hooks run at the start of a read, before its result is computed, which pins the exact
    // interleaving: everything read earlier is stale, everything read afterwards sees the other writer's commit. ----

    [Fact]
    public async Task EmptyMemberCompletedByAConcurrentApprovalBetweenThePolicyReads_IsRecognizedAsCompliant()
    {
        var (request, _) = Seed(UserStatus.Pending);
        var role = await SeedMemberRoleWithPermissionsAsync(_workspaceId);
        var workspaceRead = await GetOrSeedPermissionAsync(PermissionCodes.WorkspaceRead);

        // The first read sees the role empty. Before the assignment check runs, another approval commits its link
        // and its own membership's assignment together, as its single transaction does.
        _roles.OnHasAnyMembershipAssignment = _ =>
        {
            _roles.Seed(new RolePermission(role.Id, workspaceRead.Id));
            _roles.Seed(new WorkspaceMembershipRole(Guid.NewGuid(), role.Id));
        };
        var handler = CreateHandler();

        var result = await handler.Handle(new ApproveWorkspaceAccessRequestCommand(request.Id));

        Assert.True(result.IsSuccess);

        // Exactly one bounded re-read, and the winner's link was reused, never duplicated.
        Assert.Equal(2, _roles.GetPermissionIdsCallCount);
        Assert.Equal(1, _roles.HasAnyMembershipAssignmentCallCount);
        Assert.Empty(_roles.AddedRoles);
        Assert.Empty(_permissions.Added);
        Assert.Empty(_roles.AddedRolePermissions);
        Assert.Single(_roles.RolePermissions, link => link.RoleId == role.Id);

        var membership = Assert.Single(_memberships.Added);
        var assignment = Assert.Single(_roles.AddedMembershipAssignments);
        Assert.Equal(membership.Id, assignment.WorkspaceMembershipId);
        Assert.Equal(role.Id, assignment.RoleId);
        Assert.Equal(1, _unitOfWork.SaveChangesCallCount);
        Assert.Equal(0, _unitOfWork.DiscardTrackedChangesCallCount);
    }

    [Fact]
    public async Task EmptyMemberThatIsGenuinelyHeldWhileEmpty_FailsClosedAfterOneBoundedReRead()
    {
        var (request, user) = Seed(UserStatus.Pending);
        var role = await SeedMemberRoleWithPermissionsAsync(_workspaceId);
        _roles.Seed(new WorkspaceMembershipRole(Guid.NewGuid(), role.Id));
        var handler = CreateHandler();

        var result = await handler.Handle(new ApproveWorkspaceAccessRequestCommand(request.Id));

        AssertMemberRoleMisconfigured(result, request, user, UserStatus.Pending);
        Assert.Equal(2, _roles.GetPermissionIdsCallCount);
        Assert.Empty(_roles.RolePermissions);
    }

    [Fact]
    public async Task EmptyMemberWhoseAssignmentAppearsBetweenTheReadsWithoutALink_FailsClosed()
    {
        var (request, user) = Seed(UserStatus.Pending);
        var role = await SeedMemberRoleWithPermissionsAsync(_workspaceId);

        // Only the assignment lands: a role held while empty, which the re-read must not excuse.
        _roles.OnHasAnyMembershipAssignment = _ => _roles.Seed(new WorkspaceMembershipRole(Guid.NewGuid(), role.Id));
        var handler = CreateHandler();

        var result = await handler.Handle(new ApproveWorkspaceAccessRequestCommand(request.Id));

        AssertMemberRoleMisconfigured(result, request, user, UserStatus.Pending);
        Assert.Equal(2, _roles.GetPermissionIdsCallCount);
        Assert.Empty(_roles.RolePermissions);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task EmptyMemberWhoseRefreshedSetIsNotExactlyWorkspaceRead_FailsClosed(bool refreshedAlsoHasWorkspaceRead)
    {
        var (request, user) = Seed(UserStatus.Pending);
        var role = await SeedMemberRoleWithPermissionsAsync(_workspaceId);
        var workspaceRead = await GetOrSeedPermissionAsync(PermissionCodes.WorkspaceRead);
        var propertyRead = await GetOrSeedPermissionAsync(PermissionCodes.PropertyRead);

        _roles.OnHasAnyMembershipAssignment = _ =>
        {
            _roles.Seed(new WorkspaceMembershipRole(Guid.NewGuid(), role.Id));
            _roles.Seed(new RolePermission(role.Id, propertyRead.Id));

            if (refreshedAlsoHasWorkspaceRead)
            {
                _roles.Seed(new RolePermission(role.Id, workspaceRead.Id));
            }
        };
        var handler = CreateHandler();

        var result = await handler.Handle(new ApproveWorkspaceAccessRequestCommand(request.Id));

        // The refreshed set is judged by the same exact check, so any extra or different permission is refused.
        AssertMemberRoleMisconfigured(result, request, user, UserStatus.Pending);
        Assert.Equal(2, _roles.GetPermissionIdsCallCount);
    }

    [Fact]
    public async Task EmptyMemberWhoseReReadFails_PropagatesWithoutGrantingAnything()
    {
        var (request, user) = Seed(UserStatus.Pending);
        var role = await SeedMemberRoleWithPermissionsAsync(_workspaceId);
        _roles.Seed(new WorkspaceMembershipRole(Guid.NewGuid(), role.Id));
        _roles.OnGetPermissionIds = call =>
        {
            if (call == 2)
            {
                throw new InvalidOperationException("Simulated failure of the confirming read.");
            }
        };
        var handler = CreateHandler();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => handler.Handle(new ApproveWorkspaceAccessRequestCommand(request.Id)));

        // No business result was invented and nothing was touched or saved.
        Assert.Equal(WorkspaceAccessRequestStatus.Pending, request.Status);
        Assert.Equal(UserStatus.Pending, user.Status);
        Assert.Empty(_memberships.Added);
        Assert.Empty(_roles.AddedRoles);
        Assert.Empty(_roles.AddedRolePermissions);
        Assert.Empty(_roles.AddedMembershipAssignments);
        Assert.Empty(_permissions.Added);
        Assert.Equal(0, _unitOfWork.SaveChangesCallCount);
    }

    [Fact]
    public async Task WorkspaceReadCreatedBetweenTheLookupAndThePermissionRead_IsRecognizedAsCompliant()
    {
        var (request, _) = Seed(UserStatus.Pending);
        var role = await SeedMemberRoleWithPermissionsAsync(_workspaceId);
        var workspaceRead = new Permission(
            Guid.NewGuid(),
            PermissionCodes.WorkspaceRead,
            PermissionCodes.WorkspaceReadDescription);

        // workspace.read does not exist when the handler looks it up. Before the role's permissions are read,
        // another approval commits the permission and its link.
        _roles.OnGetPermissionIds = call =>
        {
            if (call == 1)
            {
                _permissions.Seed(workspaceRead);
                _roles.Seed(new RolePermission(role.Id, workspaceRead.Id));
            }
        };
        var handler = CreateHandler();

        var result = await handler.Handle(new ApproveWorkspaceAccessRequestCommand(request.Id));

        Assert.True(result.IsSuccess);

        // Neither the permission nor its link is created a second time.
        Assert.Empty(_permissions.Added);
        Assert.Empty(_roles.AddedRolePermissions);
        Assert.Empty(_roles.AddedRoles);

        var assignment = Assert.Single(_roles.AddedMembershipAssignments);
        Assert.Equal(role.Id, assignment.RoleId);
        Assert.Equal(1, _unitOfWork.SaveChangesCallCount);
    }

    [Fact]
    public async Task RoleWithPermissionsWhileWorkspaceReadStaysMissing_FailsClosed()
    {
        var (request, user) = Seed(UserStatus.Pending);
        var role = await SeedMemberRoleWithPermissionsAsync(_workspaceId);

        // A link to a permission row that does not exist: the refetch still finds no workspace.read.
        _roles.Seed(new RolePermission(role.Id, Guid.NewGuid()));
        var handler = CreateHandler();

        var result = await handler.Handle(new ApproveWorkspaceAccessRequestCommand(request.Id));

        AssertMemberRoleMisconfigured(result, request, user, UserStatus.Pending);
        Assert.Empty(_permissions.Added);
    }

    [Fact]
    public async Task WorkspaceReadCreatedBetweenTheReadsButTheRoleAlsoHasAnotherPermission_FailsClosed()
    {
        var (request, user) = Seed(UserStatus.Pending);
        var role = await SeedMemberRoleWithPermissionsAsync(_workspaceId);
        var workspaceRead = new Permission(
            Guid.NewGuid(),
            PermissionCodes.WorkspaceRead,
            PermissionCodes.WorkspaceReadDescription);
        var propertyRead = await GetOrSeedPermissionAsync(PermissionCodes.PropertyRead);

        _roles.OnGetPermissionIds = call =>
        {
            if (call == 1)
            {
                _permissions.Seed(workspaceRead);
                _roles.Seed(new RolePermission(role.Id, workspaceRead.Id));
                _roles.Seed(new RolePermission(role.Id, propertyRead.Id));
            }
        };
        var handler = CreateHandler();

        var result = await handler.Handle(new ApproveWorkspaceAccessRequestCommand(request.Id));

        // The refetch finds workspace.read, but the complete set is not exactly that one permission.
        AssertMemberRoleMisconfigured(result, request, user, UserStatus.Pending);
        Assert.Empty(_permissions.Added);
    }

    [Fact]
    public void MemberRoleMisconfigured_IsAControlledConflictWithAGenericCode()
    {
        var error = WorkspaceAccessRequestErrors.MemberRoleMisconfigured;

        Assert.Equal("workspace_access_requests.member_role_misconfigured", error.Code);
        Assert.Equal(SmartProperty.Common.Results.ErrorType.Conflict, error.Type);

        // The message never names a permission.
        Assert.DoesNotContain("workspace.read", error.Description);
    }

    // A policy failure is decided before anything is touched: the same loaded entities, no adds, no save.
    private void AssertMemberRoleMisconfigured(
        SmartProperty.Common.Results.Result<ApproveWorkspaceAccessRequestResult> result,
        WorkspaceAccessRequest request,
        User user,
        UserStatus userStatusBefore)
    {
        Assert.True(result.IsFailure);
        Assert.Same(WorkspaceAccessRequestErrors.MemberRoleMisconfigured, result.Error);
        Assert.Equal(SmartProperty.Common.Results.ErrorType.Conflict, result.Error!.Type);
        Assert.Equal(WorkspaceAccessRequestStatus.Pending, request.Status);
        Assert.Null(request.ReviewedByUserId);
        Assert.Null(request.ReviewedAt);
        Assert.Equal(userStatusBefore, user.Status);
        Assert.Empty(_memberships.Added);
        Assert.Empty(_roles.AddedRoles);
        Assert.Empty(_permissions.Added);
        Assert.Empty(_roles.AddedRolePermissions);
        Assert.Empty(_roles.AddedMembershipAssignments);
        Assert.Empty(_roles.AddedUserAssignments);
        Assert.Equal(0, _unitOfWork.SaveChangesCallCount);
        Assert.Equal(0, _unitOfWork.DiscardTrackedChangesCallCount);
    }

    // Seeds a Member role carrying exactly the given permission codes (none for an empty role).
    private async Task<Role> SeedMemberRoleWithPermissionsAsync(Guid workspaceId, params string[] permissionCodes)
    {
        var role = SeedMemberRoleWithPermissionsSync(workspaceId);

        foreach (var code in permissionCodes)
        {
            var permission = await GetOrSeedPermissionAsync(code);
            _roles.Seed(new RolePermission(role.Id, permission.Id));
        }

        return role;
    }

    // The permission-free part, usable inside a synchronous recovery callback.
    private Role SeedMemberRoleWithPermissionsSync(Guid workspaceId)
    {
        var role = new Role(
            Guid.NewGuid(),
            WorkspaceRoles.MemberRoleName,
            RoleScope.Workspace,
            workspaceId,
            RequestedAt);
        _roles.Seed(role);

        return role;
    }

    private async Task<Permission> GetOrSeedPermissionAsync(string code)
    {
        var permission = await _permissions.GetByCodeAsync(code);

        if (permission is null)
        {
            permission = new Permission(Guid.NewGuid(), code);
            _permissions.Seed(permission);
        }

        return permission;
    }

    private (WorkspaceAccessRequest Request, User User) Seed(UserStatus status)
    {
        var user = new User(Guid.NewGuid(), "applicant@example.test", "Applicant", "User", RequestedAt);

        switch (status)
        {
            case UserStatus.Active:
                user.Activate(RequestedAt);
                break;
            case UserStatus.Suspended:
                user.Suspend(RequestedAt);
                break;
            case UserStatus.Deactivated:
                user.Deactivate(RequestedAt);
                break;
            case UserStatus.Pending:
            default:
                break;
        }

        _users.Seed(user);

        var request = new WorkspaceAccessRequest(Guid.NewGuid(), user.Id, _workspaceId, RequestedAt);
        _accessRequests.Seed(request);

        return (request, user);
    }

    // Seeds a Member role in the given workspace, plus workspace.read, optionally already attached to it.
    private (Role Role, Permission Permission) SeedMemberRole(Guid workspaceId, bool withPermission)
    {
        var role = new Role(
            Guid.NewGuid(),
            WorkspaceRoles.MemberRoleName,
            RoleScope.Workspace,
            workspaceId,
            RequestedAt);
        _roles.Seed(role);

        var permission = _permissions.GetByCodeAsync(PermissionCodes.WorkspaceRead).GetAwaiter().GetResult();

        if (permission is null)
        {
            permission = new Permission(
                Guid.NewGuid(),
                PermissionCodes.WorkspaceRead,
                PermissionCodes.WorkspaceReadDescription);
            _permissions.Seed(permission);
        }

        if (withPermission)
        {
            _roles.Seed(new RolePermission(role.Id, permission.Id));
        }

        return (role, permission);
    }

    // What the database holds once the failed attempt has been rolled back and the tracked graph discarded: the
    // original rows, still Pending, and nothing the attempt had added.
    private (WorkspaceAccessRequest Request, User User) ReloadAfterRollBack(
        WorkspaceAccessRequest request,
        User user)
    {
        _memberships.RollBackAdded();
        _roles.RollBackAdded();
        _permissions.RollBackAdded();

        var persistedUser = new User(user.Id, user.Email, user.FirstName, user.LastName, RequestedAt);
        _users.Seed(persistedUser);

        var persistedRequest = new WorkspaceAccessRequest(request.Id, user.Id, _workspaceId, RequestedAt);
        _accessRequests.Seed(persistedRequest);

        return (persistedRequest, persistedUser);
    }

    // ---- transaction and lock coordination ----

    [Fact]
    public async Task EveryApprovalAttemptRunsInsideTheCoordinatorForThatRequest()
    {
        var (request, _) = Seed(UserStatus.Pending);
        var handler = CreateHandler();

        var result = await handler.Handle(new ApproveWorkspaceAccessRequestCommand(request.Id));

        Assert.True(result.IsSuccess);
        Assert.Equal(new[] { request.Id }, _coordinator.ExecutedFor);
        Assert.Equal(1, _unitOfWork.SaveChangesCallCount);
    }

    [Fact]
    public async Task NothingIsReadOrWrittenBeforeTheCoordinatorHasBegun()
    {
        var (request, _) = Seed(UserStatus.Pending);
        var readsWhenTheAttemptBegan = -1;
        var savesWhenTheAttemptBegan = -1;
        var clockReadsWhenTheAttemptBegan = -1;
        _coordinator.OnAttemptBegins = _ =>
        {
            readsWhenTheAttemptBegan = _users.GetByIdCallCount;
            savesWhenTheAttemptBegan = _unitOfWork.SaveChangesCallCount;
            clockReadsWhenTheAttemptBegan = _clock.UtcNowCallCount;
        };
        var handler = CreateHandler();

        var result = await handler.Handle(new ApproveWorkspaceAccessRequestCommand(request.Id));

        Assert.True(result.IsSuccess);
        Assert.Equal(0, readsWhenTheAttemptBegan);
        Assert.Equal(0, savesWhenTheAttemptBegan);
        Assert.Equal(0, clockReadsWhenTheAttemptBegan);
    }

    [Theory]
    [InlineData("lock_timeout")]
    [InlineData("deadlock")]
    public async Task ALockTimeoutOrDeadlockPropagatesWithNothingReadSavedOrRetried(string kind)
    {
        var (request, user) = Seed(UserStatus.Pending);
        var failure = new TimeoutException($"Simulated {kind}.");
        _coordinator.ExceptionBeforeOperation = failure;
        var handler = CreateHandler();

        var thrown = await Assert.ThrowsAsync<TimeoutException>(
            () => handler.Handle(new ApproveWorkspaceAccessRequestCommand(request.Id)));

        Assert.Same(failure, thrown);
        Assert.Equal(1, _coordinator.ExecuteCount);
        Assert.Equal(0, _users.GetByIdCallCount);
        Assert.Equal(0, _unitOfWork.SaveChangesCallCount);
        Assert.Equal(0, _unitOfWork.DiscardTrackedChangesCallCount);
        Assert.Equal(UserStatus.Pending, user.Status);
        Assert.Equal(WorkspaceAccessRequestStatus.Pending, request.Status);
        Assert.Empty(_memberships.Added);
        Assert.Empty(_roles.AddedRoles);
    }

    [Fact]
    public async Task AnUnauthenticatedReviewerAndAnEmptyRequestIdNeverEnterTheCoordinator()
    {
        var anonymous = CreateHandler(new FakeCurrentUser(null));
        var empty = CreateHandler();

        var first = await anonymous.Handle(new ApproveWorkspaceAccessRequestCommand(Guid.NewGuid()));
        var second = await empty.Handle(new ApproveWorkspaceAccessRequestCommand(Guid.Empty));

        Assert.Same(WorkspaceAccessRequestErrors.Unauthorized, first.Error);
        Assert.Same(WorkspaceAccessRequestErrors.NotFound, second.Error);
        Assert.Equal(0, _coordinator.ExecuteCount);
    }

    [Fact]
    public async Task ARecoveryRunsInANewAttemptOnlyAfterTheTrackedChangesAreCleared()
    {
        var (request, user) = Seed(UserStatus.Pending);
        var persistedMembership = new WorkspaceMembership(
            Guid.NewGuid(),
            user.Id,
            _workspaceId,
            ReviewedAt.AddSeconds(-1));
        var clearedWhenEachAttemptBegan = new List<int>();

        _coordinator.OnAttemptBegins = _ => clearedWhenEachAttemptBegan.Add(_unitOfWork.DiscardTrackedChangesCallCount);
        _unitOfWork.ExceptionToThrow = FakeUnitOfWork.WorkspaceMembershipUniqueConstraintViolation();
        _unitOfWork.DiscardTrackedChangesAction = () =>
        {
            _memberships.RollBackAdded();
            _roles.RollBackAdded();
            _permissions.RollBackAdded();
            _users.Seed(new User(user.Id, user.Email, user.FirstName, user.LastName, RequestedAt));
            _accessRequests.Seed(new WorkspaceAccessRequest(request.Id, user.Id, _workspaceId, RequestedAt));
            _memberships.Seed(persistedMembership);
            _unitOfWork.ExceptionToThrow = null;
        };
        var handler = CreateHandler();

        var result = await handler.Handle(new ApproveWorkspaceAccessRequestCommand(request.Id));

        Assert.True(result.IsSuccess);
        Assert.Equal(new[] { 0, 1 }, clearedWhenEachAttemptBegan);
        Assert.Equal(new[] { request.Id, request.Id }, _coordinator.ExecutedFor);
        Assert.Equal(2, _unitOfWork.SaveChangesCallCount);
    }

    [Fact]
    public async Task ACollisionThatUnrelatedFailuresFollowIsNeverRetriedAgain()
    {
        var (request, _) = Seed(UserStatus.Pending);
        var failure = new InvalidOperationException("Simulated unrelated persistence failure.");
        _unitOfWork.ExceptionToThrow = failure;
        var handler = CreateHandler();

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(
            () => handler.Handle(new ApproveWorkspaceAccessRequestCommand(request.Id)));

        Assert.Same(failure, thrown);
        Assert.Equal(1, _coordinator.ExecuteCount);
        Assert.Equal(0, _unitOfWork.DiscardTrackedChangesCallCount);
    }

    [Fact]
    public async Task WhatTheDecisionReadIsConfirmedAgainstTheLocksBeforeAnythingIsChangedOrWritten()
    {
        var (request, user) = Seed(UserStatus.Pending);
        var (role, permission) = SeedMemberRole(_workspaceId, withPermission: true);
        var stateWhenConfirmed = new List<(UserStatus User, WorkspaceAccessRequestStatus Request, int Saves)>();
        _coordinator.OnConfirm = _ => stateWhenConfirmed.Add((user.Status, request.Status, _unitOfWork.SaveChangesCallCount));
        var handler = CreateHandler();

        var result = await handler.Handle(new ApproveWorkspaceAccessRequestCommand(request.Id));

        Assert.True(result.IsSuccess);
        var confirmation = Assert.Single(_coordinator.Confirmations);
        Assert.Null(confirmation.MembershipId);
        Assert.Equal(role.Id, confirmation.RoleId);
        Assert.Equal(permission.Id, confirmation.PermissionId);
        Assert.Equal(
            (UserStatus.Pending, WorkspaceAccessRequestStatus.Pending, 0),
            Assert.Single(stateWhenConfirmed));
    }

    [Fact]
    public async Task AMissingRoleAndPermissionAreConfirmedAsMissingNotSkipped()
    {
        var (request, _) = Seed(UserStatus.Pending);
        var handler = CreateHandler();

        var result = await handler.Handle(new ApproveWorkspaceAccessRequestCommand(request.Id));

        Assert.True(result.IsSuccess);
        var confirmation = Assert.Single(_coordinator.Confirmations);
        Assert.Null(confirmation.MembershipId);
        Assert.Null(confirmation.RoleId);
        Assert.Null(confirmation.PermissionId);
    }

    [Fact]
    public async Task AnExistingMembershipIsConfirmedByItsId()
    {
        var (request, user) = Seed(UserStatus.Active);
        SeedMemberRole(_workspaceId, withPermission: true);
        var membership = new WorkspaceMembership(Guid.NewGuid(), user.Id, _workspaceId, ReviewedAt.AddSeconds(-1));
        _memberships.Seed(membership);
        var handler = CreateHandler();

        var result = await handler.Handle(new ApproveWorkspaceAccessRequestCommand(request.Id));

        Assert.True(result.IsSuccess);
        Assert.Equal(membership.Id, Assert.Single(_coordinator.Confirmations).MembershipId);
    }

    [Fact]
    public async Task ARowThatBecameVisibleAfterTheLocksAbortsTheAttemptBeforeAnyWriteAndRunsItAgainFromFreshState()
    {
        var (request, user) = Seed(UserStatus.Pending);
        SeedMemberRole(_workspaceId, withPermission: true);
        _coordinator.ReportStateChangeOnNextConfirmations = 1;
        var clearedWhenEachAttemptBegan = new List<int>();
        var savesWhenEachAttemptBegan = new List<int>();
        var userReadsWhenEachAttemptBegan = new List<int>();
        _coordinator.OnAttemptBegins = _ =>
        {
            clearedWhenEachAttemptBegan.Add(_unitOfWork.DiscardTrackedChangesCallCount);
            savesWhenEachAttemptBegan.Add(_unitOfWork.SaveChangesCallCount);
            userReadsWhenEachAttemptBegan.Add(_users.GetByIdCallCount);
        };
        var handler = CreateHandler();

        var result = await handler.Handle(new ApproveWorkspaceAccessRequestCommand(request.Id));

        Assert.True(result.IsSuccess);
        Assert.Equal(new[] { request.Id, request.Id }, _coordinator.ExecutedFor);
        Assert.Equal(2, _coordinator.Confirmations.Count);
        Assert.Equal(new[] { 0, 1 }, clearedWhenEachAttemptBegan);
        Assert.Equal(new[] { 0, 0 }, savesWhenEachAttemptBegan);
        Assert.True(userReadsWhenEachAttemptBegan[1] > userReadsWhenEachAttemptBegan[0]);
        Assert.Equal(1, _unitOfWork.SaveChangesCallCount);
        Assert.Equal(UserStatus.Active, user.Status);
        Assert.Equal(WorkspaceAccessRequestStatus.Approved, request.Status);
    }

    [Fact]
    public async Task AStateThatKeepsChangingIsBoundedAndCommitsNothing()
    {
        var (request, user) = Seed(UserStatus.Pending);
        SeedMemberRole(_workspaceId, withPermission: true);
        _coordinator.ReportStateChangeOnNextConfirmations = 10;
        var handler = CreateHandler();

        await Assert.ThrowsAsync<WorkspaceApprovalStateChangedException>(
            () => handler.Handle(new ApproveWorkspaceAccessRequestCommand(request.Id)));

        Assert.Equal(3, _coordinator.ExecuteCount);
        Assert.Equal(3, _coordinator.Confirmations.Count);
        Assert.Equal(0, _unitOfWork.SaveChangesCallCount);
        Assert.Equal(UserStatus.Pending, user.Status);
        Assert.Equal(WorkspaceAccessRequestStatus.Pending, request.Status);
        Assert.Empty(_memberships.Added);
        Assert.Empty(_roles.AddedMembershipAssignments);
    }

    [Fact]
    public async Task AMisconfiguredRoleStillFailsClosedAfterARerunAndWritesNothing()
    {
        var (request, _) = Seed(UserStatus.Pending);
        var (role, _) = SeedMemberRole(_workspaceId, withPermission: true);
        var other = new Permission(Guid.NewGuid(), "property.read", "Read properties.");
        _permissions.Seed(other);
        _roles.Seed(new RolePermission(role.Id, other.Id));
        _coordinator.ReportStateChangeOnNextConfirmations = 1;
        var handler = CreateHandler();

        var result = await handler.Handle(new ApproveWorkspaceAccessRequestCommand(request.Id));

        Assert.True(result.IsFailure);
        Assert.Equal("workspace_access_requests.member_role_misconfigured", result.Error!.Code);
        Assert.Equal(2, _coordinator.ExecuteCount);
        Assert.Equal(0, _unitOfWork.SaveChangesCallCount);
        Assert.Empty(_roles.AddedMembershipAssignments);
    }

    private ApproveWorkspaceAccessRequestCommandHandler CreateHandler(FakeCurrentUser? currentUser = null)
    {
        return new ApproveWorkspaceAccessRequestCommandHandler(
            currentUser ?? new FakeCurrentUser(_reviewerUserId),
            _accessRequests,
            _users,
            _memberships,
            _roles,
            _permissions,
            _clock,
            _unitOfWork,
            _coordinator);
    }
}
