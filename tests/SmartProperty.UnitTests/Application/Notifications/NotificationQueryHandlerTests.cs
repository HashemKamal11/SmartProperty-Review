using SmartProperty.Application.Abstractions.Persistence.Repositories;
using SmartProperty.Application.Authentication.Me;
using SmartProperty.Application.Notifications;
using SmartProperty.Application.Notifications.List;
using SmartProperty.Application.Notifications.UnreadCount;
using SmartProperty.Common.Results;
using SmartProperty.Domain.Identity;
using SmartProperty.Domain.Notifications;
using SmartProperty.UnitTests.TestDoubles;
using Xunit;

namespace SmartProperty.UnitTests.Application.Notifications;

/// <summary>
/// The list and unread-count handlers. The gate itself is covered by <see cref="NotificationRecipientGateTests"/>;
/// these tests only show that each handler uses it, stops on its answer, and passes its scope on. Nothing here proves
/// PostgreSQL behaviour.
/// </summary>
public sealed class NotificationQueryHandlerTests
{
    private static readonly Guid Me = Guid.Parse("11111111-1111-4111-8111-111111111111");
    private static readonly Guid SomeoneElse = Guid.Parse("22222222-2222-4222-8222-222222222222");
    private static readonly DateTimeOffset Created = new(2026, 11, 11, 9, 0, 0, TimeSpan.Zero);

    private readonly FakeUserRepository _users = new();
    private readonly FakeNotificationRepository _notifications = new();

    // ----- null requests

    [Fact]
    public async Task TheListHandlerRejectsANullQuery()
    {
        await Assert.ThrowsAsync<ArgumentNullException>("query", () => ListHandler(Me).Handle(null!));
    }

    [Fact]
    public async Task TheUnreadCountHandlerRejectsANullQuery()
    {
        await Assert.ThrowsAsync<ArgumentNullException>("query", () => CountHandler(Me).Handle(null!));
    }

    // ----- the gate decides first, and nothing is read on failure

    [Theory]
    [InlineData(false, null)]
    [InlineData(true, null)]
    [InlineData(true, UserStatus.Suspended)]
    public async Task TheListHandlerReturnsTheGateFailureAndReadsNoNotifications(bool hasIdentity, UserStatus? status)
    {
        var currentUserId = ArrangeGateFailure(hasIdentity, status);

        var result = await ListHandler(currentUserId).Handle(new GetMyNotificationsQuery(1, 10, UnreadOnly: true));

        Assert.True(result.IsFailure);
        Assert.Same(ExpectedGateError(status), result.Error);
        Assert.Equal(0, _notifications.CallCount);
    }

    [Theory]
    [InlineData(false, null)]
    [InlineData(true, null)]
    [InlineData(true, UserStatus.Suspended)]
    public async Task TheUnreadCountHandlerReturnsTheGateFailureAndReadsNoNotifications(bool hasIdentity, UserStatus? status)
    {
        var currentUserId = ArrangeGateFailure(hasIdentity, status);

        var result = await CountHandler(currentUserId).Handle(new GetMyUnreadNotificationCountQuery());

        Assert.True(result.IsFailure);
        Assert.Same(ExpectedGateError(status), result.Error);
        Assert.Equal(0, _notifications.CallCount);
    }

    // ----- cancellation reaches both the gate and the repository

    [Fact]
    public async Task TheListHandlerForwardsTheCancellationTokenToTheGateAndTheRepository()
    {
        using var cancellation = new CancellationTokenSource();
        var users = new RecordingUserRepository();

        await new GetMyNotificationsQueryHandler(new FakeCurrentUser(Me), users, _notifications)
            .Handle(new GetMyNotificationsQuery(null, null), cancellation.Token);

        Assert.Equal(cancellation.Token, users.LastCancellationToken);
        Assert.Equal(cancellation.Token, _notifications.LastCancellationToken);
    }

    [Fact]
    public async Task TheUnreadCountHandlerForwardsTheCancellationTokenToTheGateAndTheRepository()
    {
        using var cancellation = new CancellationTokenSource();
        var users = new RecordingUserRepository();

        await new GetMyUnreadNotificationCountQueryHandler(new FakeCurrentUser(Me), users, _notifications)
            .Handle(new GetMyUnreadNotificationCountQuery(), cancellation.Token);

        Assert.Equal(cancellation.Token, users.LastCancellationToken);
        Assert.Equal(cancellation.Token, _notifications.LastCancellationToken);
    }

    // ----- the list

    [Fact]
    public async Task TheListIsScopedToTheAuthenticatedRecipient()
    {
        SeedAccount(Me, UserStatus.Active);
        var mine = MemberAdded(Me, Created);
        _notifications.Seed(mine);
        _notifications.Seed(MemberAdded(SomeoneElse, Created.AddMinutes(1)));

        var result = await ListHandler(Me).Handle(new GetMyNotificationsQuery(null, null));

        Assert.True(result.IsSuccess);
        Assert.Equal(Me, _notifications.LastScope!.RecipientUserId);
        Assert.Equal(mine.Id, Assert.Single(result.Value.Items).Id);
        Assert.Equal(1L, result.Value.TotalCount);
    }

    [Theory]
    [InlineData(null, null, 1, 10, 0)]
    [InlineData(0, 0, 1, 1, 0)]
    [InlineData(-5, 500, 1, 100, 0)]
    [InlineData(3, 20, 3, 20, 40)]
    [InlineData(2, 101, 2, 100, 100)]
    [InlineData(10_000, 100, 10_000, 100, 999_900)]
    [InlineData(int.MaxValue, 100, 10_000, 100, 999_900)]
    public async Task PagingUsesTheSharedDefaultsAndClamping(
        int? page,
        int? pageSize,
        int expectedPage,
        int expectedPageSize,
        int expectedSkip)
    {
        SeedAccount(Me, UserStatus.Active);

        var result = await ListHandler(Me).Handle(new GetMyNotificationsQuery(page, pageSize));

        Assert.True(result.IsSuccess);
        Assert.Equal(expectedSkip, _notifications.LastSkip);
        Assert.Equal(expectedPageSize, _notifications.LastTake);
        Assert.Equal(expectedPage, result.Value.PageNumber);
        Assert.Equal(expectedPageSize, result.Value.PageSize);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task UnreadOnlyIsForwardedToTheRepository(bool unreadOnly)
    {
        SeedAccount(Me, UserStatus.Active);

        await ListHandler(Me).Handle(new GetMyNotificationsQuery(null, null, unreadOnly));

        Assert.Equal(unreadOnly, _notifications.LastUnreadOnly);
    }

    [Fact]
    public async Task UnreadOnlyDefaultsToFalse()
    {
        SeedAccount(Me, UserStatus.Active);

        await ListHandler(Me).Handle(new GetMyNotificationsQuery(null, null));

        Assert.False(_notifications.LastUnreadOnly);
    }

    [Fact]
    public async Task TheRepositoryPageIsReturnedUnchangedInOrderWithItsTotal()
    {
        SeedAccount(Me, UserStatus.Active);
        var parameters = new Dictionary<string, string>();
        var newer = new NotificationDetails(
            Guid.Parse("33333333-3333-4333-8333-333333333333"),
            NotificationCatalog.PasswordChangedType,
            null,
            null,
            parameters,
            Created.AddMinutes(5),
            null);
        var older = new NotificationDetails(
            Guid.Parse("44444444-4444-4444-8444-444444444444"),
            NotificationCatalog.MemberAddedType,
            NotificationCatalog.WorkspaceSubjectType,
            Guid.Parse("55555555-5555-4555-8555-555555555555"),
            parameters,
            Created,
            Created.AddMinutes(1));
        var repository = new ScriptedNotificationRepository { Page = new NotificationPage([newer, older], 57) };

        var result = await new GetMyNotificationsQueryHandler(new FakeCurrentUser(Me), _users, repository)
            .Handle(new GetMyNotificationsQuery(1, 2, UnreadOnly: true));

        Assert.True(result.IsSuccess);
        Assert.Equal(new[] { newer, older }, result.Value.Items);
        Assert.Equal(57L, result.Value.TotalCount);
    }

    [Fact]
    public async Task AListFailurePropagatesUnchanged()
    {
        SeedAccount(Me, UserStatus.Active);
        var failure = new InvalidOperationException("Simulated list failure.");
        _notifications.ExceptionToThrow = failure;

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(
            () => ListHandler(Me).Handle(new GetMyNotificationsQuery(null, null)));

        Assert.Same(failure, thrown);
    }

    [Fact]
    public async Task AListCancellationPropagatesUnchanged()
    {
        SeedAccount(Me, UserStatus.Active);
        var canceled = new OperationCanceledException();
        _notifications.ExceptionToThrow = canceled;

        var thrown = await Assert.ThrowsAsync<OperationCanceledException>(
            () => ListHandler(Me).Handle(new GetMyNotificationsQuery(null, null)));

        Assert.Same(canceled, thrown);
    }

    // ----- the unread count

    [Fact]
    public async Task TheUnreadCountIsTheScopedRecipientsUnreadNotifications()
    {
        SeedAccount(Me, UserStatus.Active);
        _notifications.Seed(MemberAdded(Me, Created));
        _notifications.Seed(PasswordChanged(Me, Created));
        _notifications.Seed(MemberAdded(Me, Created), readAt: Created.AddMinutes(1));
        _notifications.Seed(MemberAdded(SomeoneElse, Created));

        var result = await CountHandler(Me).Handle(new GetMyUnreadNotificationCountQuery());

        Assert.True(result.IsSuccess);
        Assert.Equal(2, result.Value);
        Assert.Equal(Me, _notifications.LastScope!.RecipientUserId);
    }

    [Fact]
    public async Task ANegativeUnreadCountIsNeverReturned()
    {
        SeedAccount(Me, UserStatus.Active);
        var repository = new ScriptedNotificationRepository { UnreadCount = -1 };

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => new GetMyUnreadNotificationCountQueryHandler(new FakeCurrentUser(Me), _users, repository)
                .Handle(new GetMyUnreadNotificationCountQuery()));
    }

    [Fact]
    public async Task AnUnreadCountFailurePropagatesUnchanged()
    {
        SeedAccount(Me, UserStatus.Active);
        var failure = new InvalidOperationException("Simulated count failure.");
        _notifications.ExceptionToThrow = failure;

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(
            () => CountHandler(Me).Handle(new GetMyUnreadNotificationCountQuery()));

        Assert.Same(failure, thrown);
    }

    [Fact]
    public async Task AnUnreadCountCancellationPropagatesUnchanged()
    {
        SeedAccount(Me, UserStatus.Active);
        var canceled = new OperationCanceledException();
        _notifications.ExceptionToThrow = canceled;

        var thrown = await Assert.ThrowsAsync<OperationCanceledException>(
            () => CountHandler(Me).Handle(new GetMyUnreadNotificationCountQuery()));

        Assert.Same(canceled, thrown);
    }

    // ----- helpers

    private GetMyNotificationsQueryHandler ListHandler(Guid? currentUserId)
    {
        return new GetMyNotificationsQueryHandler(new FakeCurrentUser(currentUserId), _users, _notifications);
    }

    private GetMyUnreadNotificationCountQueryHandler CountHandler(Guid? currentUserId)
    {
        return new GetMyUnreadNotificationCountQueryHandler(new FakeCurrentUser(currentUserId), _users, _notifications);
    }

    /// <summary>No identity; an identity without an account; or an identity whose account has the given status.</summary>
    private Guid? ArrangeGateFailure(bool hasIdentity, UserStatus? status)
    {
        if (!hasIdentity)
        {
            return null;
        }

        if (status is { } accountStatus)
        {
            SeedAccount(Me, accountStatus);
        }

        return Me;
    }

    private static Error ExpectedGateError(UserStatus? status)
    {
        return status is null ? MeErrors.Unauthorized : MeErrors.AccountUnavailable;
    }

    private void SeedAccount(Guid id, UserStatus status)
    {
        _users.Seed(Account(id, status));
    }

    private static User Account(Guid id, UserStatus status)
    {
        var user = new User(id, $"{id:N}@example.test", "Test", "User", Created);

        switch (status)
        {
            case UserStatus.Active:
                user.Activate(Created);
                break;
            case UserStatus.Suspended:
                user.Suspend(Created);
                break;
            case UserStatus.Deactivated:
                user.Deactivate(Created);
                break;
            case UserStatus.Pending:
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(status), status, "Unsupported account status.");
        }

        return user;
    }

    private static Notification MemberAdded(Guid recipientUserId, DateTimeOffset createdAt)
    {
        return Notification.CreateMemberAdded(Guid.NewGuid(), recipientUserId, Guid.NewGuid(), Guid.NewGuid(), createdAt);
    }

    private static Notification PasswordChanged(Guid recipientUserId, DateTimeOffset createdAt)
    {
        return Notification.CreatePasswordChanged(Guid.NewGuid(), recipientUserId, Guid.NewGuid(), createdAt);
    }

    /// <summary>Answers every id with an Active account for that id and records the token it was given.</summary>
    private sealed class RecordingUserRepository : IUserRepository
    {
        public CancellationToken LastCancellationToken { get; private set; }

        public Task<User?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
        {
            LastCancellationToken = cancellationToken;

            return Task.FromResult<User?>(Account(id, UserStatus.Active));
        }

        public Task<User?> GetByEmailAsync(string email, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<User?> GetByPhoneNumberAsync(string phoneNumber, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task AddAsync(User user, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }
    }

    /// <summary>Answers reads with exactly the page and count it is given, including impossible ones.</summary>
    private sealed class ScriptedNotificationRepository : INotificationRepository
    {
        public NotificationPage Page { get; init; } = new([], 0);

        public int UnreadCount { get; init; }

        public Task AddAsync(Notification notification, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<NotificationPage> ListAsync(
            NotificationVisibilityScope scope,
            bool unreadOnly,
            int skip,
            int take,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Page);
        }

        public Task<int> CountUnreadAsync(NotificationVisibilityScope scope, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(UnreadCount);
        }

        public Task<NotificationReadOutcome> MarkReadAsync(
            NotificationVisibilityScope scope,
            Guid notificationId,
            DateTimeOffset readAt,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<int> MarkDisplayedReadAsync(
            NotificationVisibilityScope scope,
            IReadOnlyCollection<Guid> notificationIds,
            DateTimeOffset readAt,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }
    }
}
