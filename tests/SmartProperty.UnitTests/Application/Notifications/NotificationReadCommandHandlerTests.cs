using SmartProperty.Application.Abstractions.Persistence.Repositories;
using SmartProperty.Application.Authentication.Me;
using SmartProperty.Application.Notifications;
using SmartProperty.Application.Notifications.MarkDisplayedRead;
using SmartProperty.Application.Notifications.MarkRead;
using SmartProperty.Common.Results;
using SmartProperty.Domain.Identity;
using SmartProperty.Domain.Notifications;
using SmartProperty.UnitTests.TestDoubles;
using Xunit;

namespace SmartProperty.UnitTests.Application.Notifications;

/// <summary>
/// The mark-one and mark-displayed handlers. The gate itself is covered by <see cref="NotificationRecipientGateTests"/>.
/// The fake repository models the read-state contract in memory; it proves nothing about PostgreSQL, concurrency or
/// the eventual in-statement eligibility check.
/// </summary>
public sealed class NotificationReadCommandHandlerTests
{
    private static readonly Guid Me = Guid.Parse("11111111-1111-4111-8111-111111111111");
    private static readonly Guid SomeoneElse = Guid.Parse("22222222-2222-4222-8222-222222222222");
    private static readonly Guid SomeNotification = Guid.Parse("33333333-3333-4333-8333-333333333333");
    private static readonly DateTimeOffset Created = new(2026, 11, 11, 9, 0, 0, TimeSpan.Zero);

    // Not UTC and not on a microsecond boundary, so normalization is observable.
    private static readonly DateTimeOffset Now =
        new DateTimeOffset(2026, 11, 11, 15, 30, 0, TimeSpan.FromHours(3)).AddTicks(1_234_567);

    private static readonly DateTimeOffset NormalizedNow = Notification.ToStoragePrecision(Now);

    private readonly FakeUserRepository _users = new();
    private readonly FakeNotificationRepository _notifications = new();
    private readonly FakeDateTimeProvider _clock = new(Now);

    // ----- null requests

    [Fact]
    public async Task TheMarkReadHandlerRejectsANullCommand()
    {
        await Assert.ThrowsAsync<ArgumentNullException>("command", () => MarkReadHandler(Me).Handle(null!));
    }

    [Fact]
    public async Task TheMarkDisplayedHandlerRejectsANullCommand()
    {
        await Assert.ThrowsAsync<ArgumentNullException>("command", () => MarkDisplayedHandler(Me).Handle(null!));
    }

    // ----- the gate decides first, even over invalid input, and nothing is read or timed on failure

    [Theory]
    [InlineData(false, null, true)]
    [InlineData(true, null, true)]
    [InlineData(true, UserStatus.Suspended, true)]
    [InlineData(false, null, false)]
    [InlineData(true, null, false)]
    [InlineData(true, UserStatus.Suspended, false)]
    public async Task MarkReadReturnsTheGateFailureBeforeAnythingElse(bool hasIdentity, UserStatus? status, bool validId)
    {
        var currentUserId = ArrangeGateFailure(hasIdentity, status);
        var notificationId = validId ? SomeNotification : Guid.Empty;

        var result = await MarkReadHandler(currentUserId).Handle(new MarkNotificationReadCommand(notificationId));

        Assert.True(result.IsFailure);
        Assert.Same(ExpectedGateError(status), result.Error);
        AssertNothingReadOrTimed();
    }

    [Theory]
    [InlineData(false, null, true)]
    [InlineData(true, null, true)]
    [InlineData(true, UserStatus.Suspended, true)]
    [InlineData(false, null, false)]
    [InlineData(true, null, false)]
    [InlineData(true, UserStatus.Suspended, false)]
    public async Task MarkDisplayedReturnsTheGateFailureBeforeAnythingElse(bool hasIdentity, UserStatus? status, bool validIds)
    {
        var currentUserId = ArrangeGateFailure(hasIdentity, status);
        Guid[]? ids = validIds ? [SomeNotification] : null;

        var result = await MarkDisplayedHandler(currentUserId).Handle(new MarkDisplayedNotificationsReadCommand(ids));

        Assert.True(result.IsFailure);
        Assert.Same(ExpectedGateError(status), result.Error);
        AssertNothingReadOrTimed();
    }

    // ----- cancellation reaches both the gate and the repository

    [Fact]
    public async Task MarkReadForwardsTheCancellationTokenToTheGateAndTheRepository()
    {
        using var cancellation = new CancellationTokenSource();
        var users = new RecordingUserRepository();

        await new MarkNotificationReadCommandHandler(new FakeCurrentUser(Me), users, _notifications, _clock)
            .Handle(new MarkNotificationReadCommand(SomeNotification), cancellation.Token);

        Assert.Equal(cancellation.Token, users.LastCancellationToken);
        Assert.Equal(cancellation.Token, _notifications.LastCancellationToken);
    }

    [Fact]
    public async Task MarkDisplayedForwardsTheCancellationTokenToTheGateAndTheRepository()
    {
        using var cancellation = new CancellationTokenSource();
        var users = new RecordingUserRepository();

        await new MarkDisplayedNotificationsReadCommandHandler(new FakeCurrentUser(Me), users, _notifications, _clock)
            .Handle(new MarkDisplayedNotificationsReadCommand([SomeNotification]), cancellation.Token);

        Assert.Equal(cancellation.Token, users.LastCancellationToken);
        Assert.Equal(cancellation.Token, _notifications.LastCancellationToken);
    }

    // ----- mark one

    [Fact]
    public async Task MarkingAnUnreadNotificationSucceedsWithTheNormalizedServerTime()
    {
        SeedAccount(Me, UserStatus.Active);
        var mine = MemberAdded(Me);
        _notifications.Seed(mine);

        var result = await MarkReadHandler(Me).Handle(new MarkNotificationReadCommand(mine.Id));

        Assert.True(result.IsSuccess);
        Assert.Equal(Me, _notifications.LastScope!.RecipientUserId);
        Assert.Equal(NormalizedNow, _notifications.LastReadAt);
        Assert.Equal(TimeSpan.Zero, _notifications.LastReadAt!.Value.Offset);
        Assert.Equal(NormalizedNow, _notifications.ReadAtOf(mine.Id));
        Assert.Equal(1, _clock.UtcNowCallCount);
    }

    [Fact]
    public async Task MarkingAnAlreadyReadNotificationSucceedsAndKeepsTheFirstReadTime()
    {
        SeedAccount(Me, UserStatus.Active);
        var mine = MemberAdded(Me);
        var firstRead = Created.AddMinutes(1);
        _notifications.Seed(mine, readAt: firstRead);

        var result = await MarkReadHandler(Me).Handle(new MarkNotificationReadCommand(mine.Id));

        Assert.True(result.IsSuccess);
        Assert.Equal(firstRead, _notifications.ReadAtOf(mine.Id));
    }

    [Fact]
    public async Task ForeignAndUnknownNotificationsAreTheSameNotFound()
    {
        SeedAccount(Me, UserStatus.Active);
        var foreign = MemberAdded(SomeoneElse);
        _notifications.Seed(foreign);

        var foreignResult = await MarkReadHandler(Me).Handle(new MarkNotificationReadCommand(foreign.Id));
        var unknownResult = await MarkReadHandler(Me).Handle(new MarkNotificationReadCommand(SomeNotification));

        AssertNotFound(foreignResult);
        AssertNotFound(unknownResult);
        Assert.Null(_notifications.ReadAtOf(foreign.Id));
    }

    [Fact]
    public async Task TheEmptyIdIsNotFoundWithoutReadingOrTiming()
    {
        SeedAccount(Me, UserStatus.Active);

        var result = await MarkReadHandler(Me).Handle(new MarkNotificationReadCommand(Guid.Empty));

        AssertNotFound(result);
        AssertNothingReadOrTimed();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(99)]
    public async Task AnUndefinedReadOutcomeIsNeverSuccess(int outcome)
    {
        SeedAccount(Me, UserStatus.Active);
        _notifications.ForcedReadOutcome = (NotificationReadOutcome)outcome;

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => MarkReadHandler(Me).Handle(new MarkNotificationReadCommand(SomeNotification)));
    }

    [Fact]
    public async Task AMarkReadFailurePropagatesUnchanged()
    {
        SeedAccount(Me, UserStatus.Active);
        var failure = new InvalidOperationException("Simulated mark failure.");
        _notifications.ExceptionToThrow = failure;

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(
            () => MarkReadHandler(Me).Handle(new MarkNotificationReadCommand(SomeNotification)));

        Assert.Same(failure, thrown);
    }

    [Fact]
    public async Task AMarkReadCancellationPropagatesUnchanged()
    {
        SeedAccount(Me, UserStatus.Active);
        var canceled = new OperationCanceledException();
        _notifications.ExceptionToThrow = canceled;

        var thrown = await Assert.ThrowsAsync<OperationCanceledException>(
            () => MarkReadHandler(Me).Handle(new MarkNotificationReadCommand(SomeNotification)));

        Assert.Same(canceled, thrown);
    }

    // ----- mark displayed: validation

    [Theory]
    [InlineData("null")]
    [InlineData("empty")]
    [InlineData("only-empty-guid")]
    [InlineData("valid-then-empty-guid")]
    [InlineData("101-distinct")]
    public async Task InvalidDisplayedIdsAreAValidationFailureWithoutReadingOrTiming(string kind)
    {
        SeedAccount(Me, UserStatus.Active);

        var result = await MarkDisplayedHandler(Me).Handle(new MarkDisplayedNotificationsReadCommand(InvalidIds(kind)));

        Assert.True(result.IsFailure);
        Assert.Equal("notifications.validation_failed", result.Error!.Code);
        Assert.Equal(ErrorType.Validation, result.Error.Type);
        AssertNothingReadOrTimed();
    }

    [Fact]
    public async Task ExactlyOneHundredDistinctIdsAreAccepted()
    {
        SeedAccount(Me, UserStatus.Active);
        var ids = DistinctIds(100);

        var result = await MarkDisplayedHandler(Me).Handle(new MarkDisplayedNotificationsReadCommand(ids));

        Assert.True(result.IsSuccess);
        Assert.Equal(ids, _notifications.LastNotificationIds);
    }

    [Fact]
    public async Task OneHundredFiftyIdsThatReduceToOneHundredDistinctAreAccepted()
    {
        SeedAccount(Me, UserStatus.Active);
        var distinct = DistinctIds(100);
        var submitted = distinct.Concat(distinct.Take(50)).ToArray();

        var result = await MarkDisplayedHandler(Me).Handle(new MarkDisplayedNotificationsReadCommand(submitted));

        Assert.Equal(150, submitted.Length);
        Assert.True(result.IsSuccess);
        Assert.Equal(distinct, _notifications.LastNotificationIds);
    }

    [Fact]
    public async Task DuplicatesAreRemovedKeepingFirstOccurrenceOrder()
    {
        SeedAccount(Me, UserStatus.Active);
        var ids = DistinctIds(3);
        var (a, b, c) = (ids[0], ids[1], ids[2]);

        await MarkDisplayedHandler(Me).Handle(new MarkDisplayedNotificationsReadCommand([b, a, b, c, a]));

        Assert.Equal(new[] { b, a, c }, _notifications.LastNotificationIds);
    }

    // ----- mark displayed: results

    [Fact]
    public async Task OnlyVisibleUnreadNotificationsAreCountedAndMarkedWithTheNormalizedServerTime()
    {
        SeedAccount(Me, UserStatus.Active);
        var unread = MemberAdded(Me);
        var alreadyRead = PasswordChanged(Me);
        var foreign = MemberAdded(SomeoneElse);
        _notifications.Seed(unread);
        _notifications.Seed(alreadyRead, readAt: Created.AddMinutes(1));
        _notifications.Seed(foreign);

        var result = await MarkDisplayedHandler(Me).Handle(
            new MarkDisplayedNotificationsReadCommand([unread.Id, alreadyRead.Id, foreign.Id, SomeNotification]));

        Assert.True(result.IsSuccess);
        Assert.Equal(1, result.Value.MarkedCount);
        Assert.Equal(Me, _notifications.LastScope!.RecipientUserId);
        Assert.Equal(NormalizedNow, _notifications.LastReadAt);
        Assert.Equal(NormalizedNow, _notifications.ReadAtOf(unread.Id));
        Assert.Equal(Created.AddMinutes(1), _notifications.ReadAtOf(alreadyRead.Id));
        Assert.Null(_notifications.ReadAtOf(foreign.Id));
        Assert.Equal(1, _clock.UtcNowCallCount);
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(3, true)]
    [InlineData(-1, false)]
    [InlineData(4, false)]
    public async Task TheMarkedCountMustBeBetweenZeroAndTheDistinctIdCount(int repositoryCount, bool accepted)
    {
        SeedAccount(Me, UserStatus.Active);
        var repository = new ScriptedNotificationRepository { MarkedCount = repositoryCount };
        var handler = new MarkDisplayedNotificationsReadCommandHandler(new FakeCurrentUser(Me), _users, repository, _clock);
        var command = new MarkDisplayedNotificationsReadCommand(DistinctIds(3));

        if (accepted)
        {
            var result = await handler.Handle(command);

            Assert.True(result.IsSuccess);
            Assert.Equal(repositoryCount, result.Value.MarkedCount);
        }
        else
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => handler.Handle(command));
        }
    }

    [Fact]
    public async Task AMarkDisplayedFailurePropagatesUnchanged()
    {
        SeedAccount(Me, UserStatus.Active);
        var failure = new InvalidOperationException("Simulated bulk mark failure.");
        _notifications.ExceptionToThrow = failure;

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(
            () => MarkDisplayedHandler(Me).Handle(new MarkDisplayedNotificationsReadCommand([SomeNotification])));

        Assert.Same(failure, thrown);
    }

    [Fact]
    public async Task AMarkDisplayedCancellationPropagatesUnchanged()
    {
        SeedAccount(Me, UserStatus.Active);
        var canceled = new OperationCanceledException();
        _notifications.ExceptionToThrow = canceled;

        var thrown = await Assert.ThrowsAsync<OperationCanceledException>(
            () => MarkDisplayedHandler(Me).Handle(new MarkDisplayedNotificationsReadCommand([SomeNotification])));

        Assert.Same(canceled, thrown);
    }

    // ----- helpers

    private MarkNotificationReadCommandHandler MarkReadHandler(Guid? currentUserId)
    {
        return new MarkNotificationReadCommandHandler(new FakeCurrentUser(currentUserId), _users, _notifications, _clock);
    }

    private MarkDisplayedNotificationsReadCommandHandler MarkDisplayedHandler(Guid? currentUserId)
    {
        return new MarkDisplayedNotificationsReadCommandHandler(
            new FakeCurrentUser(currentUserId),
            _users,
            _notifications,
            _clock);
    }

    private void AssertNothingReadOrTimed()
    {
        Assert.Equal(0, _notifications.CallCount);
        Assert.Equal(0, _clock.UtcNowCallCount);
    }

    private static void AssertNotFound(Result result)
    {
        Assert.True(result.IsFailure);
        Assert.Same(NotificationErrors.NotFound, result.Error);
        Assert.Equal("notifications.not_found", result.Error!.Code);
        Assert.Equal(ErrorType.NotFound, result.Error.Type);
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

    private static Guid[]? InvalidIds(string kind)
    {
        return kind switch
        {
            "null" => null,
            "empty" => [],
            "only-empty-guid" => [Guid.Empty],
            "valid-then-empty-guid" => [SomeNotification, Guid.Empty],
            "101-distinct" => DistinctIds(101),
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown invalid-ids case.")
        };
    }

    /// <summary>Deterministic, distinct, non-empty ids.</summary>
    private static Guid[] DistinctIds(int count)
    {
        return Enumerable.Range(1, count).Select(index => new Guid(index, 0, 0, new byte[8])).ToArray();
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

    private static Notification MemberAdded(Guid recipientUserId)
    {
        return Notification.CreateMemberAdded(Guid.NewGuid(), recipientUserId, Guid.NewGuid(), Guid.NewGuid(), Created);
    }

    private static Notification PasswordChanged(Guid recipientUserId)
    {
        return Notification.CreatePasswordChanged(Guid.NewGuid(), recipientUserId, Guid.NewGuid(), Created);
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

    /// <summary>Answers a bulk mark with exactly the count it is given, including impossible ones.</summary>
    private sealed class ScriptedNotificationRepository : INotificationRepository
    {
        public int MarkedCount { get; init; }

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
            throw new NotSupportedException();
        }

        public Task<int> CountUnreadAsync(NotificationVisibilityScope scope, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
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
            return Task.FromResult(MarkedCount);
        }
    }
}
