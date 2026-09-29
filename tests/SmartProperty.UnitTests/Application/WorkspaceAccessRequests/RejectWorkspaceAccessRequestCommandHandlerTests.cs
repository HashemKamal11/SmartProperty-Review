using SmartProperty.Application.WorkspaceAccessRequests;
using SmartProperty.Application.WorkspaceAccessRequests.Reject;
using SmartProperty.Domain.Identity;
using SmartProperty.Domain.Workspaces;
using SmartProperty.UnitTests.TestDoubles;
using Xunit;

namespace SmartProperty.UnitTests.Application.WorkspaceAccessRequests;

/// <summary>
/// Exercises the real <see cref="RejectWorkspaceAccessRequestCommandHandler"/>. Most of these assert absence: a
/// rejection records a decision and must touch nothing else.
/// </summary>
public sealed class RejectWorkspaceAccessRequestCommandHandlerTests
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

        var result = await handler.Handle(new RejectWorkspaceAccessRequestCommand(Guid.NewGuid()));

        Assert.True(result.IsFailure);
        Assert.Same(WorkspaceAccessRequestErrors.Unauthorized, result.Error);
        Assert.Equal(0, _accessRequests.GetByIdCallCount);
    }

    [Fact]
    public async Task UnknownRequest_ReturnsNotFoundAndSavesNothing()
    {
        var handler = CreateHandler();

        var result = await handler.Handle(new RejectWorkspaceAccessRequestCommand(Guid.NewGuid()));

        Assert.True(result.IsFailure);
        Assert.Same(WorkspaceAccessRequestErrors.NotFound, result.Error);
        Assert.Equal(0, _unitOfWork.SaveChangesCallCount);
    }

    [Fact]
    public async Task PendingRequest_IsRejected()
    {
        var (request, _) = Seed(UserStatus.Pending);
        var handler = CreateHandler();

        var result = await handler.Handle(new RejectWorkspaceAccessRequestCommand(request.Id));

        Assert.True(result.IsSuccess);
        Assert.Equal(WorkspaceAccessRequestStatus.Rejected, request.Status);
        Assert.Equal(WorkspaceAccessRequestStatus.Rejected, result.Value.RequestStatus);
        Assert.Equal(1, _unitOfWork.SaveChangesCallCount);
    }

    [Fact]
    public async Task PendingRequest_StoresTheAuthenticatedReviewerAndTheCurrentTime()
    {
        var (request, _) = Seed(UserStatus.Pending);
        var handler = CreateHandler();

        await handler.Handle(new RejectWorkspaceAccessRequestCommand(request.Id));

        Assert.Equal(_reviewerUserId, request.ReviewedByUserId);
        Assert.Equal(ReviewedAt, request.ReviewedAt);
    }

    [Theory]
    [InlineData(UserStatus.Pending)]
    [InlineData(UserStatus.Active)]
    [InlineData(UserStatus.Suspended)]
    [InlineData(UserStatus.Deactivated)]
    public async Task Rejection_LeavesTheAccountStatusUnchanged(UserStatus status)
    {
        var (request, user) = Seed(status);
        var updatedAtBefore = user.UpdatedAt;
        var handler = CreateHandler();

        var result = await handler.Handle(new RejectWorkspaceAccessRequestCommand(request.Id));

        Assert.True(result.IsSuccess);
        Assert.Equal(status, user.Status);
        Assert.Equal(status, result.Value.UserStatus);
        Assert.Equal(updatedAtBefore, user.UpdatedAt);
    }

    [Fact]
    public async Task Rejection_CreatesNoWorkspaceMembership()
    {
        var (request, user) = Seed(UserStatus.Pending);
        var handler = CreateHandler();

        await handler.Handle(new RejectWorkspaceAccessRequestCommand(request.Id));

        Assert.Empty(_memberships.Added);
        Assert.Null(await _memberships.GetByUserAndWorkspaceAsync(user.Id, _workspaceId));
    }

    [Fact]
    public async Task ConcurrentReviewConflict_ReturnsAlreadyReviewed()
    {
        var (request, _) = Seed(UserStatus.Pending);
        _unitOfWork.ExceptionToThrow = FakeUnitOfWork.WorkspaceAccessRequestConcurrencyConflict();
        var handler = CreateHandler();

        var result = await handler.Handle(new RejectWorkspaceAccessRequestCommand(request.Id));

        Assert.True(result.IsFailure);
        Assert.Same(WorkspaceAccessRequestErrors.AlreadyReviewed, result.Error);
        Assert.Equal(SmartProperty.Common.Results.ErrorType.Conflict, result.Error!.Type);
        Assert.Equal(1, _unitOfWork.SaveChangesCallCount);
    }

    [Fact]
    public async Task ApprovedRequest_CannotBeRejected()
    {
        var (request, _) = Seed(UserStatus.Pending);
        var approverId = Guid.NewGuid();
        request.Approve(approverId, RequestedAt);
        var handler = CreateHandler();

        var result = await handler.Handle(new RejectWorkspaceAccessRequestCommand(request.Id));

        Assert.True(result.IsFailure);
        Assert.Same(WorkspaceAccessRequestErrors.AlreadyReviewed, result.Error);
        Assert.Equal(WorkspaceAccessRequestStatus.Approved, request.Status);
        Assert.Equal(approverId, request.ReviewedByUserId);
        Assert.Equal(0, _unitOfWork.SaveChangesCallCount);
    }

    [Fact]
    public async Task AlreadyRejectedRequest_CannotBeRejectedAgain()
    {
        var (request, _) = Seed(UserStatus.Pending);
        var handler = CreateHandler();

        var first = await handler.Handle(new RejectWorkspaceAccessRequestCommand(request.Id));
        var second = await handler.Handle(new RejectWorkspaceAccessRequestCommand(request.Id));

        Assert.True(first.IsSuccess);
        Assert.True(second.IsFailure);
        Assert.Same(WorkspaceAccessRequestErrors.AlreadyReviewed, second.Error);
        Assert.Equal(1, _unitOfWork.SaveChangesCallCount);
    }

    [Fact]
    public async Task MissingUserRow_IsRefused()
    {
        var request = new WorkspaceAccessRequest(Guid.NewGuid(), Guid.NewGuid(), _workspaceId, RequestedAt);
        _accessRequests.Seed(request);
        var handler = CreateHandler();

        var result = await handler.Handle(new RejectWorkspaceAccessRequestCommand(request.Id));

        Assert.True(result.IsFailure);
        Assert.Same(WorkspaceAccessRequestErrors.TargetAccountUnavailable, result.Error);
        Assert.Equal(WorkspaceAccessRequestStatus.Pending, request.Status);
        Assert.Equal(0, _unitOfWork.SaveChangesCallCount);
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

    private RejectWorkspaceAccessRequestCommandHandler CreateHandler(FakeCurrentUser? currentUser = null)
    {
        return new RejectWorkspaceAccessRequestCommandHandler(
            currentUser ?? new FakeCurrentUser(_reviewerUserId),
            _accessRequests,
            _users,
            _clock,
            _unitOfWork);
    }
}
