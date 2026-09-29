using SmartProperty.Application.Abstractions.Persistence;
using SmartProperty.Application.WorkspaceAccessRequests;
using SmartProperty.Application.WorkspaceAccessRequests.Approve;
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
/// set, which is the point.
/// </remarks>
public sealed class ApproveWorkspaceAccessRequestCommandHandlerTests
{
    private static readonly DateTimeOffset RequestedAt = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset ReviewedAt = new(2026, 2, 1, 9, 30, 0, TimeSpan.Zero);

    private readonly FakeWorkspaceAccessRequestRepository _accessRequests = new();
    private readonly FakeUserRepository _users = new();
    private readonly FakeWorkspaceMembershipRepository _memberships = new();
    private readonly FakeDateTimeProvider _clock = new(ReviewedAt);
    private readonly FakeUnitOfWork _unitOfWork = new();
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

        // The decision, the activation, and the membership all land together or not at all.
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
        Assert.Equal(2, _unitOfWork.SaveChangesCallCount);
        Assert.Equal(1, _unitOfWork.DiscardTrackedChangesCallCount);
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
        Assert.Empty(_memberships.Added);
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

    private ApproveWorkspaceAccessRequestCommandHandler CreateHandler(FakeCurrentUser? currentUser = null)
    {
        return new ApproveWorkspaceAccessRequestCommandHandler(
            currentUser ?? new FakeCurrentUser(_reviewerUserId),
            _accessRequests,
            _users,
            _memberships,
            _clock,
            _unitOfWork);
    }
}
