using System.Collections.Immutable;
using System.Reflection;
using SmartProperty.Application.Abstractions.Identity;
using SmartProperty.Application.Abstractions.Persistence.Repositories;
using SmartProperty.Application.Authentication.Me;
using SmartProperty.Application.Notifications;
using SmartProperty.Common.Results;
using SmartProperty.Domain.Identity;
using SmartProperty.Domain.Notifications;
using SmartProperty.UnitTests.TestDoubles;
using Xunit;

namespace SmartProperty.UnitTests.Application.Notifications;

/// <summary>
/// The gate, the scope it issues, the error catalog, and the repository contract as the fake models it. Nothing here
/// proves PostgreSQL behaviour: in-statement eligibility, isolation and concurrency belong to the persistence tests.
/// </summary>
public sealed class NotificationRecipientGateTests
{
    private static readonly Guid Me = Guid.Parse("11111111-1111-4111-8111-111111111111");
    private static readonly Guid SomeoneElse = Guid.Parse("22222222-2222-4222-8222-222222222222");
    private static readonly DateTimeOffset Created = new(2026, 11, 11, 9, 0, 0, TimeSpan.Zero);

    private readonly FakeUserRepository _users = new();

    // ----- dependencies

    [Fact]
    public async Task ANullCurrentUserIsRejected()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(
            "currentUser",
            () => NotificationRecipientGate.AuthorizeAsync(null!, _users));
    }

    [Fact]
    public async Task ANullUserRepositoryIsRejected()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(
            "users",
            () => NotificationRecipientGate.AuthorizeAsync(new FakeCurrentUser(Me), null!));
    }

    // ----- identity

    [Fact]
    public async Task AMissingIdentityIsUnauthorizedAndNoAccountIsRead()
    {
        var result = await NotificationRecipientGate.AuthorizeAsync(new FakeCurrentUser(null), _users);

        AssertUnauthorized(result);
        Assert.Equal(0, _users.GetByIdCallCount);
    }

    [Fact]
    public async Task AnEmptyIdentityIsUnauthorizedAndNoAccountIsRead()
    {
        var result = await NotificationRecipientGate.AuthorizeAsync(new FakeCurrentUser(Guid.Empty), _users);

        AssertUnauthorized(result);
        Assert.Equal(0, _users.GetByIdCallCount);
    }

    [Fact]
    public async Task AnIdentityWithNoAccountIsUnauthorized()
    {
        var result = await NotificationRecipientGate.AuthorizeAsync(new FakeCurrentUser(Me), _users);

        AssertUnauthorized(result);
        Assert.Equal(1, _users.GetByIdCallCount);
    }

    [Fact]
    public async Task AnAccountRecordForADifferentIdIsUnauthorizedEvenWhenActive()
    {
        var users = new RecordingUserRepository(_ => Account(SomeoneElse, UserStatus.Active));

        var result = await NotificationRecipientGate.AuthorizeAsync(new FakeCurrentUser(Me), users);

        AssertUnauthorized(result);
        Assert.Equal(Me, Assert.Single(users.RequestedIds));
    }

    [Fact]
    public async Task TheIdentityIsReadOnceAndThatCapturedValueIsLookedUpAndScoped()
    {
        var currentUser = new SequenceCurrentUser(Me, SomeoneElse);
        var users = new RecordingUserRepository(id => Account(id, UserStatus.Active));

        var result = await NotificationRecipientGate.AuthorizeAsync(currentUser, users);

        Assert.True(result.IsSuccess);
        Assert.Equal(1, currentUser.UserIdReadCount);
        Assert.Equal(Me, Assert.Single(users.RequestedIds));
        Assert.Equal(Me, result.Value.RecipientUserId);
    }

    // ----- account standing

    [Theory]
    [InlineData(UserStatus.Pending)]
    [InlineData(UserStatus.Suspended)]
    [InlineData(UserStatus.Deactivated)]
    public async Task ANonActiveAccountIsAccountUnavailable(UserStatus status)
    {
        _users.Seed(Account(Me, status));

        var result = await NotificationRecipientGate.AuthorizeAsync(new FakeCurrentUser(Me), _users);

        Assert.True(result.IsFailure);
        Assert.Same(MeErrors.AccountUnavailable, result.Error);
        Assert.Equal("authentication.account_unavailable", result.Error!.Code);
        Assert.Equal(ErrorType.Forbidden, result.Error.Type);
    }

    [Fact]
    public async Task AnActiveAccountReceivesAClosedPersonalScopeForItself()
    {
        _users.Seed(Account(Me, UserStatus.Active));

        var result = await NotificationRecipientGate.AuthorizeAsync(new FakeCurrentUser(Me), _users);

        Assert.True(result.IsSuccess);
        Assert.Equal(Me, result.Value.RecipientUserId);
        Assert.Equal(new[] { NotificationVisibilityKind.Personal }, result.Value.VisibilityKinds.ToArray());
        Assert.Equal(NotificationCatalog.ActiveTypes.ToArray(), result.Value.Types.ToArray());
    }

    // ----- failures and cancellation

    [Fact]
    public async Task TheCancellationTokenReachesTheAccountLookup()
    {
        using var cancellation = new CancellationTokenSource();
        var users = new RecordingUserRepository(id => Account(id, UserStatus.Active));

        await NotificationRecipientGate.AuthorizeAsync(new FakeCurrentUser(Me), users, cancellation.Token);

        Assert.Equal(cancellation.Token, users.LastCancellationToken);
    }

    [Fact]
    public async Task ALookupFailurePropagatesUnchanged()
    {
        var failure = new InvalidOperationException("Simulated user lookup failure.");
        var users = new RecordingUserRepository(_ => null) { ExceptionToThrow = failure };

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(
            () => NotificationRecipientGate.AuthorizeAsync(new FakeCurrentUser(Me), users));

        Assert.Same(failure, thrown);
    }

    [Fact]
    public async Task ALookupCancellationPropagatesUnchanged()
    {
        using var cancellation = new CancellationTokenSource();
        var canceled = new OperationCanceledException(cancellation.Token);
        var users = new RecordingUserRepository(_ => null) { ExceptionToThrow = canceled };

        var thrown = await Assert.ThrowsAsync<OperationCanceledException>(
            () => NotificationRecipientGate.AuthorizeAsync(new FakeCurrentUser(Me), users, cancellation.Token));

        Assert.Same(canceled, thrown);
    }

    // ----- the scope

    [Fact]
    public void TheScopeCannotBeConstructedOrChangedFromOutside()
    {
        var scopeType = typeof(NotificationVisibilityScope);

        Assert.True(scopeType.IsSealed);
        Assert.Empty(scopeType.GetConstructors());
        Assert.All(
            scopeType.GetProperties(BindingFlags.Public | BindingFlags.Instance),
            property => Assert.Null(property.GetSetMethod(nonPublic: true)));
    }

    [Fact]
    public void TheScopeFactoryRejectsAnEmptyRecipient()
    {
        Assert.Throws<ArgumentException>(
            "recipientUserId",
            () => NotificationVisibilityScope.ForVerifiedRecipient(Guid.Empty));
    }

    [Fact]
    public void TheScopeCollectionsAreImmutable()
    {
        var scope = NotificationVisibilityScope.ForVerifiedRecipient(Me);
        var scopeType = typeof(NotificationVisibilityScope);

        Assert.Equal(
            typeof(ImmutableArray<NotificationVisibilityKind>),
            scopeType.GetProperty(nameof(NotificationVisibilityScope.VisibilityKinds))!.PropertyType);
        Assert.Equal(
            typeof(ImmutableArray<string>),
            scopeType.GetProperty(nameof(NotificationVisibilityScope.Types))!.PropertyType);

        IList<NotificationVisibilityKind> kinds = scope.VisibilityKinds;
        IList<string> types = scope.Types;

        Assert.Throws<NotSupportedException>(() => kinds.Add((NotificationVisibilityKind)2));
        Assert.Throws<NotSupportedException>(() => types.Add("workspace.member_removed"));
        Assert.Throws<NotSupportedException>(() => types[0] = "tampered");

        Assert.Equal(new[] { NotificationVisibilityKind.Personal }, scope.VisibilityKinds.ToArray());
        Assert.Equal(NotificationCatalog.ActiveTypes.ToArray(), scope.Types.ToArray());
    }

    // ----- errors

    [Fact]
    public void TheErrorCatalogUsesTheApprovedCodesAndTypes()
    {
        Assert.Same(MeErrors.Unauthorized, NotificationErrors.Unauthorized);
        Assert.Equal("authentication.unauthorized", NotificationErrors.Unauthorized.Code);
        Assert.Equal(ErrorType.Unauthorized, NotificationErrors.Unauthorized.Type);

        Assert.Same(MeErrors.AccountUnavailable, NotificationErrors.AccountUnavailable);
        Assert.Equal("authentication.account_unavailable", NotificationErrors.AccountUnavailable.Code);
        Assert.Equal(ErrorType.Forbidden, NotificationErrors.AccountUnavailable.Type);

        Assert.Equal("notifications.not_found", NotificationErrors.NotFound.Code);
        Assert.Equal(ErrorType.NotFound, NotificationErrors.NotFound.Type);

        var validation = NotificationErrors.ValidationFailed("At least one notification id is required.");

        Assert.Equal("notifications.validation_failed", validation.Code);
        Assert.Equal(ErrorType.Validation, validation.Type);
        Assert.Equal("At least one notification id is required.", validation.Description);

        Assert.Equal(100, NotificationErrors.MaxDisplayedIds);
    }

    // ----- the repository contract, as the fake models it

    [Fact]
    public async Task ListAndCountSeeOnlyTheScopedRecipientAndUnreadOnlyFiltersBeforeTotalAndPaging()
    {
        var repository = new FakeNotificationRepository();
        var oldest = MemberAdded(Me, Created);
        var read = MemberAdded(Me, Created.AddMinutes(1));
        var newest = PasswordChanged(Me, Created.AddMinutes(2));
        var foreign = PasswordChanged(SomeoneElse, Created.AddMinutes(3));

        repository.Seed(oldest);
        repository.Seed(read, readAt: Created.AddMinutes(5));
        repository.Seed(newest);
        repository.Seed(foreign);

        var scope = NotificationVisibilityScope.ForVerifiedRecipient(Me);

        var all = await repository.ListAsync(scope, unreadOnly: false, skip: 0, take: 10);
        var unreadFirstPage = await repository.ListAsync(scope, unreadOnly: true, skip: 0, take: 1);

        Assert.Equal(new[] { newest.Id, read.Id, oldest.Id }, all.Items.Select(item => item.Id).ToArray());
        Assert.Equal(3L, all.TotalCount);
        Assert.Equal(newest.Id, Assert.Single(unreadFirstPage.Items).Id);
        Assert.Equal(2L, unreadFirstPage.TotalCount);
        Assert.Equal(2, await repository.CountUnreadAsync(scope));
    }

    [Fact]
    public async Task AnIneligibleRecipientSeesNothingAndChangesNothing()
    {
        var repository = new FakeNotificationRepository { RecipientEligible = false };
        var mine = MemberAdded(Me, Created);
        repository.Seed(mine);

        var scope = NotificationVisibilityScope.ForVerifiedRecipient(Me);

        var page = await repository.ListAsync(scope, unreadOnly: false, skip: 0, take: 10);

        Assert.Empty(page.Items);
        Assert.Equal(0L, page.TotalCount);
        Assert.Equal(0, await repository.CountUnreadAsync(scope));
        Assert.Equal(NotificationReadOutcome.NotFound, await repository.MarkReadAsync(scope, mine.Id, Created));
        Assert.Equal(0, await repository.MarkDisplayedReadAsync(scope, [mine.Id], Created));
        Assert.Null(repository.ReadAtOf(mine.Id));
    }

    [Fact]
    public async Task MarkReadDistinguishesMarkedReadAlreadyReadAndNotFoundAndKeepsTheFirstReadTime()
    {
        var repository = new FakeNotificationRepository();
        var mine = MemberAdded(Me, Created);
        var foreign = MemberAdded(SomeoneElse, Created);
        repository.Seed(mine);
        repository.Seed(foreign);

        var scope = NotificationVisibilityScope.ForVerifiedRecipient(Me);
        var firstRead = Created.AddMinutes(10).AddTicks(7);
        var expectedReadAt = Notification.ToStoragePrecision(firstRead);

        Assert.Equal(NotificationReadOutcome.MarkedRead, await repository.MarkReadAsync(scope, mine.Id, firstRead));
        Assert.Equal(expectedReadAt, repository.ReadAtOf(mine.Id));

        Assert.Equal(
            NotificationReadOutcome.AlreadyRead,
            await repository.MarkReadAsync(scope, mine.Id, Created.AddMinutes(20)));
        Assert.Equal(expectedReadAt, repository.ReadAtOf(mine.Id));

        Assert.Equal(NotificationReadOutcome.NotFound, await repository.MarkReadAsync(scope, foreign.Id, firstRead));
        Assert.Null(repository.ReadAtOf(foreign.Id));

        Assert.Equal(
            NotificationReadOutcome.NotFound,
            await repository.MarkReadAsync(scope, Guid.Parse("33333333-3333-4333-8333-333333333333"), firstRead));
    }

    [Fact]
    public async Task AReadTimeEarlierThanCreationIsStoredAsTheCreationTime()
    {
        var repository = new FakeNotificationRepository();
        var mine = MemberAdded(Me, Created);
        repository.Seed(mine);

        var scope = NotificationVisibilityScope.ForVerifiedRecipient(Me);

        await repository.MarkReadAsync(scope, mine.Id, Created.AddMinutes(-5));

        Assert.Equal(mine.CreatedAt, repository.ReadAtOf(mine.Id));
    }

    [Fact]
    public async Task MarkDisplayedReadCountsOnlyVisibleNotificationsThisCallMarked()
    {
        var repository = new FakeNotificationRepository();
        var unread = MemberAdded(Me, Created);
        var alreadyRead = PasswordChanged(Me, Created);
        var notDisplayed = MemberAdded(Me, Created);
        var foreign = MemberAdded(SomeoneElse, Created);
        repository.Seed(unread);
        repository.Seed(alreadyRead, readAt: Created.AddMinutes(1));
        repository.Seed(notDisplayed);
        repository.Seed(foreign);

        var scope = NotificationVisibilityScope.ForVerifiedRecipient(Me);
        var unknown = Guid.Parse("44444444-4444-4444-8444-444444444444");

        var marked = await repository.MarkDisplayedReadAsync(
            scope,
            [unread.Id, alreadyRead.Id, foreign.Id, unknown],
            Created.AddMinutes(30));

        Assert.Equal(1, marked);
        Assert.Equal(Created.AddMinutes(30), repository.ReadAtOf(unread.Id));
        Assert.Equal(Created.AddMinutes(1), repository.ReadAtOf(alreadyRead.Id));
        Assert.Null(repository.ReadAtOf(notDisplayed.Id));
        Assert.Null(repository.ReadAtOf(foreign.Id));
    }

    [Fact]
    public async Task AddOnlyTracksANotificationAndDoesNotMakeItVisible()
    {
        var repository = new FakeNotificationRepository();
        var notification = MemberAdded(Me, Created);

        await repository.AddAsync(notification);

        Assert.Same(notification, Assert.Single(repository.Tracked));
        Assert.Equal(
            0L,
            (await repository.ListAsync(
                NotificationVisibilityScope.ForVerifiedRecipient(Me),
                unreadOnly: false,
                skip: 0,
                take: 10)).TotalCount);
    }

    // ----- helpers

    private static void AssertUnauthorized(Result<NotificationVisibilityScope> result)
    {
        Assert.True(result.IsFailure);
        Assert.Same(MeErrors.Unauthorized, result.Error);
        Assert.Equal("authentication.unauthorized", result.Error!.Code);
        Assert.Equal(ErrorType.Unauthorized, result.Error.Type);
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

    /// <summary>Answers a different identity on every read, so a second read by the gate would be visible.</summary>
    private sealed class SequenceCurrentUser(params Guid?[] answers) : ICurrentUser
    {
        public int UserIdReadCount { get; private set; }

        public Guid? UserId
        {
            get
            {
                var answer = answers[Math.Min(UserIdReadCount, answers.Length - 1)];
                UserIdReadCount++;
                return answer;
            }
        }

        public bool IsAuthenticated => true;
    }

    /// <summary>Records each id lookup and its token, and can answer with any user or throw.</summary>
    private sealed class RecordingUserRepository(Func<Guid, User?> answer) : IUserRepository
    {
        public List<Guid> RequestedIds { get; } = [];

        public CancellationToken LastCancellationToken { get; private set; }

        public Exception? ExceptionToThrow { get; init; }

        public Task<User?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
        {
            RequestedIds.Add(id);
            LastCancellationToken = cancellationToken;

            if (ExceptionToThrow is { } exception)
            {
                throw exception;
            }

            return Task.FromResult(answer(id));
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
}
