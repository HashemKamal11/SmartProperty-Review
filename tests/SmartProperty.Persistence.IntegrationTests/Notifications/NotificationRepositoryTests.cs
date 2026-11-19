using System.Collections;
using System.Data.Common;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using SmartProperty.Application.Abstractions.Persistence;
using SmartProperty.Application.Abstractions.Persistence.Repositories;
using SmartProperty.Application.Notifications;
using SmartProperty.Domain.Identity;
using SmartProperty.Domain.Notifications;
using SmartProperty.Persistence.Context;
using SmartProperty.Persistence.IntegrationTests.Infrastructure;
using Xunit;

namespace SmartProperty.Persistence.IntegrationTests.Notifications;

/// <summary>
/// The real <see cref="INotificationRepository"/> against PostgreSQL (R1–R17): every scoped statement enforces the
/// recipient, the account's current Active status, the visibility kinds and the closed type list by itself.
/// </summary>
/// <remarks>
/// Scopes are built with the real internal <c>NotificationVisibilityScope.ForVerifiedRecipient</c>, reached by a
/// narrowly checked reflection call, purely as test arrangement: the Application gate is not under test here, the
/// repository's own SQL is. Persisted state is always verified through independent connections.
/// </remarks>
public sealed class NotificationRepositoryTests(PostgreSqlFixture fixture) : DatabaseTest(fixture)
{
    private static readonly Func<Guid, NotificationVisibilityScope> CreateScope = BindScopeFactory();

    // Whole seconds after the fixed test clock, so every value is already at storage precision.
    private static readonly DateTimeOffset CreatedAt = TestClock.DefaultNow.AddMinutes(1);

    private readonly CommandRecorder recorder = new();

    private protected override IInterceptor[] ExtraInterceptors()
    {
        return [recorder];
    }

    // R1
    [Fact]
    public async Task R1_InvalidArgumentsAreRejectedBeforeAnyDatabaseCommand()
    {
        var scope = CreateScope(Guid.NewGuid());
        var id = Guid.NewGuid();
        var tooMany = Enumerable.Range(0, 101).Select(_ => Guid.NewGuid()).ToArray();
        recorder.Reset();

        await using (var session = OpenSession())
        {
            var repository = session.Repository;

            await Assert.ThrowsAsync<ArgumentNullException>(() => repository.ListAsync(null!, false, 0, 10));
            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => repository.ListAsync(scope, false, -1, 10));
            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => repository.ListAsync(scope, false, 0, 0));
            await Assert.ThrowsAsync<ArgumentNullException>(() => repository.CountUnreadAsync(null!));
            await Assert.ThrowsAsync<ArgumentNullException>(() => repository.MarkReadAsync(null!, id, CreatedAt));
            await Assert.ThrowsAsync<ArgumentNullException>(
                () => repository.MarkDisplayedReadAsync(null!, [id], CreatedAt));
            await Assert.ThrowsAsync<ArgumentNullException>(
                () => repository.MarkDisplayedReadAsync(scope, null!, CreatedAt));
            await Assert.ThrowsAsync<ArgumentException>(
                () => repository.MarkDisplayedReadAsync(scope, Array.Empty<Guid>(), CreatedAt));
            await Assert.ThrowsAsync<ArgumentException>(
                () => repository.MarkDisplayedReadAsync(scope, tooMany, CreatedAt));
            await Assert.ThrowsAsync<ArgumentException>(
                () => repository.MarkDisplayedReadAsync(scope, [id, Guid.Empty], CreatedAt));
            await Assert.ThrowsAsync<ArgumentException>(
                () => repository.MarkDisplayedReadAsync(scope, [id, id], CreatedAt));
            await Assert.ThrowsAsync<ArgumentNullException>(() => repository.AddAsync(null!));
        }

        Assert.Empty(recorder.Attempted);

        // Exactly the bound is accepted: one statement, nothing matched.
        var exactlyTheBound = Enumerable.Range(0, 100).Select(_ => Guid.NewGuid()).ToArray();

        await using (var session = OpenSession())
        {
            Assert.Equal(0, await session.Repository.MarkDisplayedReadAsync(scope, exactlyTheBound, CreatedAt));
        }

        Assert.Single(recorder.Attempted);
    }

    // R2
    [Fact]
    public async Task R2_AddOnlyTracksTheNotificationUntilTheUnitOfWorkSaves()
    {
        var recipient = await SeedUserAsync();
        var notification = Notification.CreatePasswordChanged(Guid.NewGuid(), recipient, Guid.NewGuid(), CreatedAt);
        recorder.Reset();

        await using var session = OpenSession();
        await session.Repository.AddAsync(notification);

        Assert.Empty(recorder.Attempted);
        Assert.Equal(EntityState.Added, session.Context.Entry(notification).State);
        Assert.False(await NotificationExistsAsync(notification.Id));

        var unitOfWork = session.Services.GetRequiredService<IUnitOfWork>();
        await unitOfWork.SaveChangesAsync();

        Assert.True(await NotificationExistsAsync(notification.Id));
    }

    // R3
    [Fact]
    public async Task R3_EveryScopedMethodIsBoundToTheExactRecipient()
    {
        var alice = await SeedUserAsync();
        var bob = await SeedUserAsync();
        var aliceUnread = await SeedNotificationAsync(alice, CreatedAt);
        var bobUnread = await SeedNotificationAsync(bob, CreatedAt.AddSeconds(1));
        var bobBefore = await RowSnapshotAsync(bobUnread);
        var scope = CreateScope(alice);
        var readAt = CreatedAt.AddMinutes(5);
        recorder.Reset();

        await using var session = OpenSession();
        var page = await session.Repository.ListAsync(scope, false, 0, 10);
        var unread = await session.Repository.CountUnreadAsync(scope);
        var foreign = await session.Repository.MarkReadAsync(scope, bobUnread, readAt);
        var marked = await session.Repository.MarkDisplayedReadAsync(scope, [aliceUnread, bobUnread], readAt);

        var item = Assert.Single(page.Items);
        Assert.Equal(aliceUnread, item.Id);
        Assert.Equal(1L, page.TotalCount);
        Assert.Equal(1, unread);
        Assert.Equal(NotificationReadOutcome.NotFound, foreign);
        Assert.Equal(1, marked);
        Assert.Equal(readAt, await ReadAtAsync(aliceUnread));
        Assert.Null(await ReadAtAsync(bobUnread));
        Assert.Equal(bobBefore, await RowSnapshotAsync(bobUnread));
    }

    // R4
    [Theory]
    [InlineData("Pending")]
    [InlineData("Suspended")]
    [InlineData("Deactivated")]
    public async Task R4_AScopeIssuedWhileActiveGrantsNothingOnceTheAccountIsInactive(string inactiveStatus)
    {
        var recipient = await SeedUserAsync();
        var first = await SeedNotificationAsync(recipient, CreatedAt);
        var second = await SeedNotificationAsync(recipient, CreatedAt.AddSeconds(1));

        // Captured while the account is Active, exactly as the gate would have issued it.
        var scope = CreateScope(recipient);
        await SetUserStatusAsync(recipient, inactiveStatus);
        var readAt = CreatedAt.AddMinutes(5);

        await using (var session = OpenSession())
        {
            var page = await session.Repository.ListAsync(scope, false, 0, 10);
            Assert.Empty(page.Items);
            Assert.Equal(0L, page.TotalCount);
            Assert.Equal(0, await session.Repository.CountUnreadAsync(scope));
            Assert.Equal(NotificationReadOutcome.NotFound, await session.Repository.MarkReadAsync(scope, first, readAt));
            Assert.Equal(0, await session.Repository.MarkDisplayedReadAsync(scope, [first, second], readAt));
        }

        Assert.Null(await ReadAtAsync(first));
        Assert.Null(await ReadAtAsync(second));

        await SetUserStatusAsync(recipient, "Active");

        await using (var session = OpenSession())
        {
            var page = await session.Repository.ListAsync(scope, false, 0, 10);
            Assert.Equal(2L, page.TotalCount);
            Assert.Equal(2, await session.Repository.CountUnreadAsync(scope));
            Assert.Equal(NotificationReadOutcome.MarkedRead, await session.Repository.MarkReadAsync(scope, first, readAt));
            Assert.Equal(1, await session.Repository.MarkDisplayedReadAsync(scope, [first, second], readAt));
        }

        Assert.Equal(readAt, await ReadAtAsync(first));
        Assert.Equal(readAt, await ReadAtAsync(second));
    }

    // R5
    [Fact]
    public async Task R5_AScopeForAUserThatDoesNotExistGrantsNothing()
    {
        // No notification ever references the missing id, so the recipient foreign key is never violated.
        var other = await SeedUserAsync();
        var control = await SeedNotificationAsync(other, CreatedAt);
        var before = await RowSnapshotAsync(control);
        var scope = CreateScope(Guid.NewGuid());
        var readAt = CreatedAt.AddMinutes(5);

        await using var session = OpenSession();
        var page = await session.Repository.ListAsync(scope, false, 0, 10);

        Assert.Empty(page.Items);
        Assert.Equal(0L, page.TotalCount);
        Assert.Equal(0, await session.Repository.CountUnreadAsync(scope));
        Assert.Equal(NotificationReadOutcome.NotFound, await session.Repository.MarkReadAsync(scope, control, readAt));
        Assert.Equal(0, await session.Repository.MarkDisplayedReadAsync(scope, [control], readAt));
        Assert.Equal(before, await RowSnapshotAsync(control));
    }

    // R6
    [Fact]
    public async Task R6_AnUnsupportedTypeIsNeverVisibleInAPrivateCompatibilityDatabase()
    {
        // Only this private copy drops the type check, and the subject check that also names the two types.
        await using var compatibility = await CompatibilityDatabase.CreateAsync(
            Fixture,
            "ALTER TABLE notifications.notifications DROP CONSTRAINT ck_notifications_type, DROP CONSTRAINT ck_notifications_subject;");

        var recipient = await compatibility.SeedActiveUserAsync();
        var control = await compatibility.SeedPasswordChangedAsync(recipient, CreatedAt);
        var incompatible = Guid.NewGuid();
        await compatibility.InsertRawAsync(incompatible, recipient, "other.kind", visibilityKind: 1, CreatedAt.AddSeconds(1));

        await AssertOnlyTheControlIsVisibleAsync(compatibility, recipient, control, incompatible);
    }

    // R7
    [Fact]
    public async Task R7_AnUnsupportedVisibilityKindIsNeverVisibleInAPrivateCompatibilityDatabase()
    {
        // A separate private copy that drops only the visibility-kind check.
        await using var compatibility = await CompatibilityDatabase.CreateAsync(
            Fixture,
            "ALTER TABLE notifications.notifications DROP CONSTRAINT ck_notifications_visibility_kind;");

        var recipient = await compatibility.SeedActiveUserAsync();
        var control = await compatibility.SeedPasswordChangedAsync(recipient, CreatedAt);
        var incompatible = Guid.NewGuid();
        await compatibility.InsertRawAsync(
            incompatible,
            recipient,
            NotificationCatalog.PasswordChangedType,
            visibilityKind: 2,
            CreatedAt.AddSeconds(1));

        await AssertOnlyTheControlIsVisibleAsync(compatibility, recipient, control, incompatible);
    }

    // R8
    [Fact]
    public async Task R8_UnreadOnlyIsAppliedBeforeTheTotalAndThePage()
    {
        var recipient = await SeedUserAsync();
        var ids = new Guid[5];

        for (var index = 0; index < ids.Length; index++)
        {
            ids[index] = await SeedNotificationAsync(recipient, CreatedAt.AddMinutes(index));
        }

        await SetReadAtAsync(ids[1], CreatedAt.AddHours(1));
        await SetReadAtAsync(ids[3], CreatedAt.AddHours(1));
        var scope = CreateScope(recipient);
        recorder.Reset();

        await using var session = OpenSession();
        var first = await session.Repository.ListAsync(scope, true, 0, 2);
        var second = await session.Repository.ListAsync(scope, true, 2, 2);
        var all = await session.Repository.ListAsync(scope, false, 0, 10);

        Assert.Equal(3L, first.TotalCount);
        Assert.Equal(3L, second.TotalCount);
        Assert.Equal([ids[4], ids[2]], first.Items.Select(item => item.Id));
        Assert.Equal([ids[0]], second.Items.Select(item => item.Id));
        Assert.All(first.Items.Concat(second.Items), item => Assert.Null(item.ReadAt));
        Assert.Equal(5L, all.TotalCount);
        Assert.Equal(Enumerable.Reverse(ids), all.Items.Select(item => item.Id));

        // Each list is exactly one total and one page.
        Assert.Equal(6, recorder.Attempted.Count);
    }

    // R9
    [Fact]
    public async Task R9_EqualCreationTimesAreOrderedByIdDescendingAsPostgreSqlComparesUuids()
    {
        var recipient = await SeedUserAsync();
        var tied = new List<Guid>();

        for (var index = 0; index < 6; index++)
        {
            tied.Add(await SeedNotificationAsync(recipient, CreatedAt));
        }

        var newest = await SeedNotificationAsync(recipient, CreatedAt.AddSeconds(1));

        // PostgreSQL compares uuid values byte by byte in RFC 4122 order, which is the ordinal order of their
        // canonical lower-case text.
        var expected = new[] { newest }
            .Concat(tied.OrderByDescending(id => id.ToString("D"), StringComparer.Ordinal))
            .ToArray();

        await using var session = OpenSession();
        var page = await session.Repository.ListAsync(CreateScope(recipient), false, 0, 10);

        Assert.Equal(expected, page.Items.Select(item => item.Id));
    }

    // R10
    [Fact]
    public async Task R10_MarkReadReturnsEachDefinedOutcome()
    {
        var recipient = await SeedUserAsync();
        var unread = await SeedNotificationAsync(recipient, CreatedAt);
        var alreadyRead = await SeedNotificationAsync(recipient, CreatedAt.AddSeconds(1));
        var firstRead = CreatedAt.AddMinutes(2);
        await SetReadAtAsync(alreadyRead, firstRead);
        var scope = CreateScope(recipient);
        var readAt = CreatedAt.AddMinutes(5);

        await using var session = OpenSession();

        recorder.Reset();
        Assert.Equal(NotificationReadOutcome.MarkedRead, await session.Repository.MarkReadAsync(scope, unread, readAt));
        Assert.Equal(["UPDATE"], recorder.CompletedKinds());

        recorder.Reset();
        Assert.Equal(NotificationReadOutcome.AlreadyRead, await session.Repository.MarkReadAsync(scope, alreadyRead, readAt));
        Assert.Equal(["UPDATE", "SELECT"], recorder.CompletedKinds());

        recorder.Reset();
        Assert.Equal(NotificationReadOutcome.NotFound, await session.Repository.MarkReadAsync(scope, Guid.NewGuid(), readAt));
        Assert.Equal(["UPDATE", "SELECT"], recorder.CompletedKinds());

        Assert.Equal(readAt, await ReadAtAsync(unread));
        Assert.Equal(firstRead, await ReadAtAsync(alreadyRead));
    }

    // R11
    [Fact]
    public async Task R11_BulkMarkingCountsOnlyVisibleUnreadRowsAndTouchesNothingElse()
    {
        var recipient = await SeedUserAsync();
        var other = await SeedUserAsync();
        var firstUnread = await SeedNotificationAsync(recipient, CreatedAt);
        var secondUnread = await SeedNotificationAsync(recipient, CreatedAt.AddSeconds(1));
        var alreadyRead = await SeedNotificationAsync(recipient, CreatedAt.AddSeconds(2));
        await SetReadAtAsync(alreadyRead, CreatedAt.AddMinutes(1));
        var foreign = await SeedNotificationAsync(other, CreatedAt);
        var alreadyReadBefore = await RowSnapshotAsync(alreadyRead);
        var foreignBefore = await RowSnapshotAsync(foreign);
        var readAt = CreatedAt.AddMinutes(5);
        recorder.Reset();

        await using var session = OpenSession();
        var changed = await session.Repository.MarkDisplayedReadAsync(
            CreateScope(recipient),
            [firstUnread, secondUnread, alreadyRead, Guid.NewGuid(), foreign],
            readAt);

        Assert.Equal(2, changed);
        Assert.Equal(["UPDATE"], recorder.CompletedKinds());
        Assert.Equal(readAt, await ReadAtAsync(firstUnread));
        Assert.Equal(readAt, await ReadAtAsync(secondUnread));
        Assert.Equal(alreadyReadBefore, await RowSnapshotAsync(alreadyRead));
        Assert.Equal(foreignBefore, await RowSnapshotAsync(foreign));
    }

    // R12
    [Fact]
    public async Task R12_TheFirstReadTimeIsNeverOverwritten()
    {
        var recipient = await SeedUserAsync();
        var id = await SeedNotificationAsync(recipient, CreatedAt);
        var scope = CreateScope(recipient);
        var first = CreatedAt.AddMinutes(1);

        await using var session = OpenSession();
        Assert.Equal(NotificationReadOutcome.MarkedRead, await session.Repository.MarkReadAsync(scope, id, first));
        Assert.Equal(NotificationReadOutcome.AlreadyRead, await session.Repository.MarkReadAsync(scope, id, first.AddMinutes(1)));
        Assert.Equal(0, await session.Repository.MarkDisplayedReadAsync(scope, [id], first.AddMinutes(2)));

        Assert.Equal(first, await ReadAtAsync(id));
    }

    // R13
    [Fact]
    public async Task R13_CreationAndReadTimesRoundTripAsUtcMicroseconds()
    {
        var recipient = await SeedUserAsync();

        // Seven sub-microsecond ticks and a non-UTC offset on both values.
        var created = new DateTimeOffset(2026, 3, 1, 15, 30, 45, TimeSpan.FromHours(3)).AddTicks(1_234_567);
        var expectedCreated = Notification.ToStoragePrecision(created);
        Assert.Equal(TimeSpan.Zero, expectedCreated.Offset);
        Assert.NotEqual(created.UtcTicks, expectedCreated.UtcTicks);

        var later = await SeedNotificationAsync(recipient, created);
        var earlier = await SeedNotificationAsync(recipient, created.AddDays(-1));
        var readAt = created.AddMinutes(10).AddTicks(7);
        var scope = CreateScope(recipient);

        await using var session = OpenSession();
        Assert.Equal(NotificationReadOutcome.MarkedRead, await session.Repository.MarkReadAsync(scope, later, readAt));

        // A read time earlier than the notification's creation is stored as the creation time.
        Assert.Equal(
            NotificationReadOutcome.MarkedRead,
            await session.Repository.MarkReadAsync(scope, earlier, created.AddDays(-2)));

        var page = await session.Repository.ListAsync(scope, false, 0, 10);
        var laterItem = Assert.Single(page.Items, item => item.Id == later);
        var earlierItem = Assert.Single(page.Items, item => item.Id == earlier);

        Assert.Equal(expectedCreated, laterItem.CreatedAt);
        Assert.Equal(TimeSpan.Zero, laterItem.CreatedAt.Offset);
        Assert.Equal(Notification.ToStoragePrecision(readAt), laterItem.ReadAt);
        Assert.Equal(TimeSpan.Zero, laterItem.ReadAt!.Value.Offset);
        Assert.Equal(Notification.ToStoragePrecision(readAt), await ReadAtAsync(later));
        Assert.Equal(earlierItem.CreatedAt, earlierItem.ReadAt);
        Assert.Equal(Notification.ToStoragePrecision(created.AddDays(-1)), await ReadAtAsync(earlier));
    }

    // R14
    [Fact]
    public async Task R14_MarkReadNeitherSavesNorDiscardsAnUnrelatedPendingAddition()
    {
        var recipient = await SeedUserAsync();
        var id = await SeedNotificationAsync(recipient, CreatedAt);
        var readAt = CreatedAt.AddMinutes(5);

        await using var session = OpenSession();
        var pending = AccessGraph.ActiveUser($"pending-{Guid.NewGuid():n}@example.test");
        session.Context.Users.Add(pending);
        recorder.Reset();

        Assert.Equal(
            NotificationReadOutcome.MarkedRead,
            await session.Repository.MarkReadAsync(CreateScope(recipient), id, readAt));

        Assert.Equal(EntityState.Added, session.Context.Entry(pending).State);
        Assert.False(await UserExistsAsync(pending.Id));
        Assert.DoesNotContain(recorder.Attempted, entry => entry.Text.Contains("INSERT", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(readAt, await ReadAtAsync(id));
    }

    // R15
    [Fact]
    public async Task R15_BulkMarkingNeitherSavesNorDiscardsAnUnrelatedPendingAddition()
    {
        var recipient = await SeedUserAsync();
        var id = await SeedNotificationAsync(recipient, CreatedAt);
        var readAt = CreatedAt.AddMinutes(5);

        await using var session = OpenSession();
        var pending = AccessGraph.ActiveUser($"pending-{Guid.NewGuid():n}@example.test");
        session.Context.Users.Add(pending);
        recorder.Reset();

        Assert.Equal(1, await session.Repository.MarkDisplayedReadAsync(CreateScope(recipient), [id], readAt));

        Assert.Equal(EntityState.Added, session.Context.Entry(pending).State);
        Assert.False(await UserExistsAsync(pending.Id));
        Assert.DoesNotContain(recorder.Attempted, entry => entry.Text.Contains("INSERT", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(readAt, await ReadAtAsync(id));
    }

    // R16
    [Fact]
    public async Task R16_ATrackedNotificationKeepsItsLoadedReadStateAfterTheSetBasedUpdate()
    {
        var recipient = await SeedUserAsync();
        var id = await SeedNotificationAsync(recipient, CreatedAt);
        var readAt = CreatedAt.AddMinutes(5);

        await using var session = OpenSession();
        var tracked = await session.Context.Set<Notification>().SingleAsync(notification => notification.Id == id);

        Assert.Equal(
            NotificationReadOutcome.MarkedRead,
            await session.Repository.MarkReadAsync(CreateScope(recipient), id, readAt));

        Assert.Null(tracked.ReadAt);
        Assert.Equal(EntityState.Unchanged, session.Context.Entry(tracked).State);
        Assert.Equal(readAt, await ReadAtAsync(id));
    }

    // R17
    [Fact]
    public async Task R17_EveryScopedStatementCarriesTheScopePredicatesWithTheirActualValues()
    {
        var recipient = await SeedUserAsync();
        var unread = await SeedNotificationAsync(recipient, CreatedAt);
        var alreadyRead = await SeedNotificationAsync(recipient, CreatedAt.AddSeconds(1));
        await SetReadAtAsync(alreadyRead, CreatedAt.AddMinutes(1));
        var scope = CreateScope(recipient);
        recorder.Reset();

        await using (var session = OpenSession())
        {
            await session.Repository.ListAsync(scope, false, 0, 10);
            await session.Repository.CountUnreadAsync(scope);
            Assert.Equal(
                NotificationReadOutcome.AlreadyRead,
                await session.Repository.MarkReadAsync(scope, alreadyRead, CreatedAt.AddMinutes(5)));
            Assert.Equal(1, await session.Repository.MarkDisplayedReadAsync(scope, [unread], CreatedAt.AddMinutes(5)));
        }

        // List total, list page, unread count, update and probe, bulk update.
        var statements = recorder.Completed()
            .Where(entry => entry.Text.Contains("notifications", StringComparison.OrdinalIgnoreCase))
            .ToList();
        Assert.Equal(6, statements.Count);

        foreach (var statement in statements)
        {
            ScopeEvidence.AssertScoped(statement, scope);
        }

        // Behaviour, not text, is the primary evidence; the outcomes above already proved the predicates hold.
        Assert.Equal(CreatedAt.AddMinutes(5), await ReadAtAsync(unread));
    }

    private static async Task AssertOnlyTheControlIsVisibleAsync(
        CompatibilityDatabase compatibility,
        Guid recipient,
        Guid control,
        Guid incompatible)
    {
        var scope = CreateScope(recipient);
        var readAt = CreatedAt.AddMinutes(5);

        await using (var session = new Session(compatibility.Host.CreateScope()))
        {
            var repository = session.Repository;
            var page = await repository.ListAsync(scope, false, 0, 10);

            var item = Assert.Single(page.Items);
            Assert.Equal(control, item.Id);
            Assert.Equal(1L, page.TotalCount);
            Assert.Equal(1, await repository.CountUnreadAsync(scope));
            Assert.Equal(NotificationReadOutcome.NotFound, await repository.MarkReadAsync(scope, incompatible, readAt));
            Assert.Equal(1, await repository.MarkDisplayedReadAsync(scope, [control, incompatible], readAt));
        }

        Assert.Null(await compatibility.ReadAtAsync(incompatible));
        Assert.Equal(readAt, await compatibility.ReadAtAsync(control));
    }

    private Session OpenSession()
    {
        return new Session(Host.CreateScope());
    }

    private async Task<Guid> SeedUserAsync()
    {
        var user = AccessGraph.ActiveUser($"notif-{Guid.NewGuid():n}@example.test");

        await using var context = Host.CreateVerificationContext();
        context.Users.Add(user);
        await context.SaveChangesAsync();

        return user.Id;
    }

    private async Task<Guid> SeedNotificationAsync(Guid recipient, DateTimeOffset createdAt)
    {
        var notification = Notification.CreateMemberAdded(Guid.NewGuid(), recipient, Guid.NewGuid(), Guid.NewGuid(), createdAt);

        await using var context = Host.CreateVerificationContext();
        context.Set<Notification>().Add(notification);
        await context.SaveChangesAsync();

        return notification.Id;
    }

    private Task SetReadAtAsync(Guid id, DateTimeOffset readAt)
    {
        return ExecuteAsync(
            Host.ConnectionString,
            "UPDATE notifications.notifications SET read_at = @readAt WHERE id = @id",
            ("readAt", readAt),
            ("id", id));
    }

    private Task SetUserStatusAsync(Guid userId, string status)
    {
        return ExecuteAsync(
            Host.ConnectionString,
            "UPDATE identity.users SET status = @status, updated_at = @updatedAt WHERE id = @id",
            ("status", status),
            ("updatedAt", TestClock.DefaultNow.AddHours(1)),
            ("id", userId));
    }

    private Task<DateTimeOffset?> ReadAtAsync(Guid id)
    {
        return ReadAtAsync(Host.ConnectionString, id);
    }

    private async Task<bool> NotificationExistsAsync(Guid id)
    {
        return await ScalarAsync<long>(
            Host.ConnectionString,
            "SELECT count(*) FROM notifications.notifications WHERE id = @id",
            ("id", id)) == 1L;
    }

    private async Task<bool> UserExistsAsync(Guid id)
    {
        return await ScalarAsync<long>(
            Host.ConnectionString,
            "SELECT count(*) FROM identity.users WHERE id = @id",
            ("id", id)) == 1L;
    }

    private Task<string> RowSnapshotAsync(Guid id)
    {
        return ScalarAsync<string>(
            Host.ConnectionString,
            "SELECT row_to_json(n)::text FROM notifications.notifications AS n WHERE n.id = @id",
            ("id", id));
    }

    private static async Task ExecuteAsync(
        string connectionString,
        string sql,
        params (string Name, object? Value)[] parameters)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);

        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }

        await command.ExecuteNonQueryAsync();
    }

    private static async Task<T> ScalarAsync<T>(
        string connectionString,
        string sql,
        params (string Name, object? Value)[] parameters)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);

        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }

        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync(), $"No row returned by: {sql}");

        return reader.GetFieldValue<T>(0);
    }

    private static async Task<DateTimeOffset?> ReadAtAsync(string connectionString, Guid id)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "SELECT read_at FROM notifications.notifications WHERE id = @id",
            connection);
        command.Parameters.AddWithValue("id", id);
        await using var reader = await command.ExecuteReaderAsync();

        Assert.True(await reader.ReadAsync(), $"Notification {id} does not exist.");

        return reader.IsDBNull(0) ? null : reader.GetFieldValue<DateTimeOffset>(0);
    }

    private static Func<Guid, NotificationVisibilityScope> BindScopeFactory()
    {
        var method = typeof(NotificationVisibilityScope).GetMethod(
            "ForVerifiedRecipient",
            BindingFlags.NonPublic | BindingFlags.Static,
            binder: null,
            types: [typeof(Guid)],
            modifiers: null)
            ?? throw new InvalidOperationException(
                "NotificationVisibilityScope.ForVerifiedRecipient(Guid) was not found; the test-only scope factory no longer matches production.");

        if (method.ReturnType != typeof(NotificationVisibilityScope) || !method.IsAssembly)
        {
            throw new InvalidOperationException($"Unexpected scope factory signature: {method}.");
        }

        return method.CreateDelegate<Func<Guid, NotificationVisibilityScope>>();
    }

    /// <summary>One request: a service scope, its context and its repository.</summary>
    private sealed class Session : IAsyncDisposable
    {
        private readonly IServiceScope scope;

        public Session(IServiceScope scope)
        {
            this.scope = scope;
            Repository = scope.ServiceProvider.GetRequiredService<INotificationRepository>();
            Context = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        }

        public IServiceProvider Services => scope.ServiceProvider;

        public INotificationRepository Repository { get; }

        public ApplicationDbContext Context { get; }

        public ValueTask DisposeAsync()
        {
            if (scope is IAsyncDisposable asyncDisposable)
            {
                return asyncDisposable.DisposeAsync();
            }

            scope.Dispose();

            return ValueTask.CompletedTask;
        }
    }

    /// <summary>
    /// A private database built by migrations, in which only the named checks are relaxed. The shared template and
    /// this test's own database are never altered.
    /// </summary>
    private sealed class CompatibilityDatabase : IAsyncDisposable
    {
        private CompatibilityDatabase(string connectionString, PersistenceTestHost host)
        {
            ConnectionString = connectionString;
            Host = host;
        }

        public string ConnectionString { get; }

        public PersistenceTestHost Host { get; }

        public static async Task<CompatibilityDatabase> CreateAsync(PostgreSqlFixture fixture, string relaxSql)
        {
            var connectionString = await fixture.CreateDatabaseAsync();

            await using (var context = PostgreSqlFixture.CreateContext(connectionString))
            {
                await context.Database.EnsureDeletedAsync();
                await context.Database.MigrateAsync();
            }

            NpgsqlConnection.ClearPool(new NpgsqlConnection(connectionString));
            await ExecuteAsync(connectionString, relaxSql);

            return new CompatibilityDatabase(connectionString, PersistenceTestHost.Create(connectionString));
        }

        public async Task<Guid> SeedActiveUserAsync()
        {
            var user = AccessGraph.ActiveUser($"compat-{Guid.NewGuid():n}@example.test");

            await using var context = PostgreSqlFixture.CreateContext(ConnectionString);
            context.Users.Add(user);
            await context.SaveChangesAsync();

            return user.Id;
        }

        public async Task<Guid> SeedPasswordChangedAsync(Guid recipient, DateTimeOffset createdAt)
        {
            var notification = Notification.CreatePasswordChanged(Guid.NewGuid(), recipient, Guid.NewGuid(), createdAt);

            await using var context = PostgreSqlFixture.CreateContext(ConnectionString);
            context.Set<Notification>().Add(notification);
            await context.SaveChangesAsync();

            return notification.Id;
        }

        public Task InsertRawAsync(Guid id, Guid recipient, string type, short visibilityKind, DateTimeOffset createdAt)
        {
            return ExecuteAsync(
                ConnectionString,
                """
                INSERT INTO notifications.notifications
                    (id, recipient_user_id, type, occurrence_id, visibility_kind, subject_type, subject_id, parameters, created_at, read_at)
                VALUES (@id, @recipient, @type, @occurrence, @kind, NULL, NULL, '{}'::jsonb, @createdAt, NULL)
                """,
                ("id", id),
                ("recipient", recipient),
                ("type", type),
                ("occurrence", Guid.NewGuid()),
                ("kind", visibilityKind),
                ("createdAt", createdAt));
        }

        public Task<DateTimeOffset?> ReadAtAsync(Guid id)
        {
            return NotificationRepositoryTests.ReadAtAsync(ConnectionString, id);
        }

        public async ValueTask DisposeAsync()
        {
            await Host.DisposeAsync();
            NpgsqlConnection.ClearPool(new NpgsqlConnection(ConnectionString));
        }
    }

    private enum CommandStatus
    {
        Attempted,
        Executed,
        ReaderCreated,
        Failed
    }

    private sealed record ParameterSnapshot(string Name, object? Value, string? Error);

    private sealed class CommandEntry(string text, IReadOnlyList<ParameterSnapshot> parameters)
    {
        public string Text { get; } = text;

        public IReadOnlyList<ParameterSnapshot> Parameters { get; } = parameters;

        public CommandStatus Status { get; set; } = CommandStatus.Attempted;

        public string Kind => Text.TrimStart().StartsWith("UPDATE", StringComparison.OrdinalIgnoreCase) ? "UPDATE"
            : Text.TrimStart().StartsWith("SELECT", StringComparison.OrdinalIgnoreCase) ? "SELECT"
            : "OTHER";
    }

    /// <summary>
    /// Records every command the host's contexts attempt, and separately whether it executed, created a reader or
    /// failed. The SQL text and a snapshot of each parameter value are taken when the command is about to execute.
    /// A reader callback proves only that a reader was created, not that its rows were consumed.
    /// </summary>
    private sealed class CommandRecorder : DbCommandInterceptor
    {
        private readonly Lock sync = new();
        private readonly List<CommandEntry> entries = [];
        private readonly Dictionary<Guid, CommandEntry> byCommandId = [];

        public IReadOnlyList<CommandEntry> Attempted
        {
            get
            {
                lock (sync)
                {
                    return [.. entries];
                }
            }
        }

        public IReadOnlyList<CommandEntry> Completed()
        {
            lock (sync)
            {
                return [.. entries.Where(entry => entry.Status is CommandStatus.Executed or CommandStatus.ReaderCreated)];
            }
        }

        public IReadOnlyList<string> CompletedKinds()
        {
            return [.. Completed().Select(entry => entry.Kind)];
        }

        public void Reset()
        {
            lock (sync)
            {
                entries.Clear();
                byCommandId.Clear();
            }
        }

        public override InterceptionResult<DbDataReader> ReaderExecuting(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
        {
            Attempt(command, eventData);
            return result;
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            Attempt(command, eventData);
            return ValueTask.FromResult(result);
        }

        public override DbDataReader ReaderExecuted(DbCommand command, CommandExecutedEventData eventData, DbDataReader result)
        {
            Complete(eventData.CommandId, CommandStatus.ReaderCreated);
            return result;
        }

        public override ValueTask<DbDataReader> ReaderExecutedAsync(
            DbCommand command,
            CommandExecutedEventData eventData,
            DbDataReader result,
            CancellationToken cancellationToken = default)
        {
            Complete(eventData.CommandId, CommandStatus.ReaderCreated);
            return ValueTask.FromResult(result);
        }

        public override InterceptionResult<int> NonQueryExecuting(
            DbCommand command, CommandEventData eventData, InterceptionResult<int> result)
        {
            Attempt(command, eventData);
            return result;
        }

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            Attempt(command, eventData);
            return ValueTask.FromResult(result);
        }

        public override int NonQueryExecuted(DbCommand command, CommandExecutedEventData eventData, int result)
        {
            Complete(eventData.CommandId, CommandStatus.Executed);
            return result;
        }

        public override ValueTask<int> NonQueryExecutedAsync(
            DbCommand command,
            CommandExecutedEventData eventData,
            int result,
            CancellationToken cancellationToken = default)
        {
            Complete(eventData.CommandId, CommandStatus.Executed);
            return ValueTask.FromResult(result);
        }

        public override InterceptionResult<object> ScalarExecuting(
            DbCommand command, CommandEventData eventData, InterceptionResult<object> result)
        {
            Attempt(command, eventData);
            return result;
        }

        public override ValueTask<InterceptionResult<object>> ScalarExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<object> result,
            CancellationToken cancellationToken = default)
        {
            Attempt(command, eventData);
            return ValueTask.FromResult(result);
        }

        public override object? ScalarExecuted(DbCommand command, CommandExecutedEventData eventData, object? result)
        {
            Complete(eventData.CommandId, CommandStatus.Executed);
            return result;
        }

        public override ValueTask<object?> ScalarExecutedAsync(
            DbCommand command,
            CommandExecutedEventData eventData,
            object? result,
            CancellationToken cancellationToken = default)
        {
            Complete(eventData.CommandId, CommandStatus.Executed);
            return ValueTask.FromResult(result);
        }

        public override void CommandFailed(DbCommand command, CommandErrorEventData eventData)
        {
            Complete(eventData.CommandId, CommandStatus.Failed);
        }

        public override Task CommandFailedAsync(
            DbCommand command,
            CommandErrorEventData eventData,
            CancellationToken cancellationToken = default)
        {
            Complete(eventData.CommandId, CommandStatus.Failed);
            return Task.CompletedTask;
        }

        private void Attempt(DbCommand command, CommandEventData eventData)
        {
            var parameters = new List<ParameterSnapshot>();

            foreach (DbParameter parameter in command.Parameters)
            {
                var (value, error) = SnapshotValue(parameter.Value);
                parameters.Add(new ParameterSnapshot(parameter.ParameterName, value, error));
            }

            var entry = new CommandEntry(command.CommandText, parameters);

            lock (sync)
            {
                entries.Add(entry);
                byCommandId[eventData.CommandId] = entry;
            }
        }

        private void Complete(Guid commandId, CommandStatus status)
        {
            lock (sync)
            {
                if (byCommandId.TryGetValue(commandId, out var entry))
                {
                    entry.Status = status;
                }
            }
        }

        // Immutable values are kept as they are; arrays and other sequences are copied element by element, so a later
        // change to the caller's collection cannot alter the record. Anything else is reported, never guessed at.
        private static (object? Value, string? Error) SnapshotValue(object? value)
        {
            if (value is null or DBNull)
            {
                return (null, null);
            }

            if (IsImmutable(value))
            {
                return (value, null);
            }

            if (value is IEnumerable sequence)
            {
                var copy = new List<object?>();

                foreach (var element in sequence)
                {
                    if (element is not null && !IsImmutable(element))
                    {
                        return (null, $"Unsupported parameter element type {element.GetType().FullName}.");
                    }

                    copy.Add(element);
                }

                return (copy.ToArray(), null);
            }

            return (null, $"Unsupported parameter representation {value.GetType().FullName}.");
        }

        private static bool IsImmutable(object value)
        {
            return value is string or Guid or bool or byte or short or int or long or decimal or double or float
                or DateTime or DateTimeOffset or TimeSpan
                || value.GetType().IsEnum;
        }
    }

    /// <summary>
    /// Supporting evidence that a statement carries all four scope predicates, as a positive conjunction, with the
    /// scope's actual values bound to the predicates that use them. A deliberately narrow recognizer for the
    /// repository's SELECT and UPDATE shapes only: the original text and parameter snapshots are never altered,
    /// literals keep their raw token and decoded value separately, aliases are bound before any column is read, and
    /// anything outside the known grammar fails with the full statement rather than being skipped.
    /// </summary>
    private static class ScopeEvidence
    {
        private static readonly HashSet<string> KnownQuotedIdentifiers = new(StringComparer.Ordinal)
        {
            "notifications", "identity", "users", "id", "recipient_user_id", "visibility_kind", "type", "status", "read_at", "created_at"
        };

        private static readonly HashSet<string> ReservedWords = new(StringComparer.OrdinalIgnoreCase)
        {
            "select", "from", "where", "and", "or", "not", "exists", "is", "null", "in", "any", "as", "set", "update",
            "order", "by", "limit", "offset", "case", "when", "then", "else", "end", "asc", "desc", "nulls", "first", "last"
        };

        private static readonly Regex AliasShape = new("^[a-z][a-z0-9_]*$", RegexOptions.CultureInvariant);

        private enum TokenKind
        {
            Word,
            QuotedIdentifier,
            Literal,
            Parameter,
            Number,
            Punctuation
        }

        // Raw is the token exactly as written. Value is the decoded content: the literal value with '' decoded, the
        // quoted identifier with "" decoded, the parameter name without '@', or the word or symbol as written.
        private sealed record Token(TokenKind Kind, string Raw, string Value)
        {
            public bool IsIdentifier => Kind == TokenKind.QuotedIdentifier || (Kind == TokenKind.Word && !ReservedWords.Contains(Value));

            // Only unquoted identifiers are case-folded; a decoded quoted identifier is compared exactly.
            public string IdentifierName => Kind == TokenKind.Word ? Value.ToLowerInvariant() : Value;

            public bool IsWord(string word)
            {
                return Kind == TokenKind.Word && string.Equals(Value, word, StringComparison.OrdinalIgnoreCase);
            }

            public bool IsSymbol(string symbol)
            {
                return Kind == TokenKind.Punctuation && string.Equals(Value, symbol, StringComparison.Ordinal);
            }
        }

        private enum ElementContext
        {
            Scalar,
            Array
        }

        public static void AssertScoped(CommandEntry statement, NotificationVisibilityScope scope)
        {
            Assert.All(statement.Parameters, parameter => Assert.Null(parameter.Error));

            var tokens = Tokenize(statement);
            var (outerAlias, whereStart, whereEnd) = LocateWhere(statement, tokens);
            var conjuncts = SplitConjuncts(statement, tokens, whereStart, whereEnd);

            var recipientPredicates = 0;
            var kindPredicates = 0;
            var typePredicates = 0;
            var eligibilityPredicates = 0;

            foreach (var (start, end) in conjuncts)
            {
                if (tokens[start].IsWord("exists"))
                {
                    AssertEligibility(statement, tokens, start, end, outerAlias, scope);
                    eligibilityPredicates++;
                    continue;
                }

                var position = start;
                var column = ReadColumn(statement, tokens, ref position, end, allowedQualifier: outerAlias, rejectedQualifier: null);

                switch (column)
                {
                    case "recipient_user_id":
                        Expect(statement, tokens, ref position, end, "=");
                        var recipient = ReadElement(statement, tokens, ref position, end, ElementContext.Scalar);
                        ExpectEnd(statement, tokens, position, end);
                        Require(statement, recipient.Count == 1 && recipient[0] is Guid recipientId && recipientId == scope.RecipientUserId,
                            $"the recipient predicate is not bound to {scope.RecipientUserId}");
                        recipientPredicates++;
                        break;

                    case "visibility_kind":
                        var kinds = ReadMembership(statement, tokens, position, end);
                        AssertSetEquals(
                            statement,
                            "visibility kinds",
                            kinds.Select(value => ToKind(statement, value)).ToList(),
                            scope.VisibilityKinds.Select(kind => (long)kind).ToHashSet());
                        kindPredicates++;
                        break;

                    case "type":
                        var types = ReadMembership(statement, tokens, position, end);
                        AssertSetEquals(
                            statement,
                            "notification types",
                            types.Select(value => value is string text ? text : throw Reject(statement, $"a type element is not a string: {value ?? "null"}")).ToList(),
                            scope.Types.ToHashSet(StringComparer.Ordinal));
                        typePredicates++;
                        break;

                    case "read_at":
                        // Narrowing only: unread rows.
                        ExpectWord(statement, tokens, ref position, end, "is");
                        ExpectWord(statement, tokens, ref position, end, "null");
                        ExpectEnd(statement, tokens, position, end);
                        break;

                    case "id":
                        // Narrowing only: one notification, or the displayed set.
                        var ids = ReadMembership(statement, tokens, position, end);
                        Require(statement, ids.Count > 0 && ids.All(value => value is Guid id && id != Guid.Empty), "an id filter element is not a non-empty Guid");
                        break;

                    default:
                        throw Reject(statement, $"unsupported predicate on column '{column}': {Render(tokens, start, end)}");
                }
            }

            Require(statement, recipientPredicates == 1, $"expected exactly one recipient predicate, found {recipientPredicates}");
            Require(statement, kindPredicates == 1, $"expected exactly one visibility-kind predicate, found {kindPredicates}");
            Require(statement, typePredicates == 1, $"expected exactly one type predicate, found {typePredicates}");
            Require(statement, eligibilityPredicates == 1, $"expected exactly one Active-account EXISTS predicate, found {eligibilityPredicates}");
        }

        // EXISTS ( SELECT 1 FROM identity.users [AS] u WHERE u.id = @recipient AND u.status = 'Active' )
        private static void AssertEligibility(
            CommandEntry statement,
            Token[] tokens,
            int start,
            int end,
            string outerAlias,
            NotificationVisibilityScope scope)
        {
            var position = start + 1;
            Expect(statement, tokens, ref position, end, "(");
            var close = FindClose(statement, tokens, position - 1, end);
            Require(statement, close == end - 1, $"unexpected tokens after the EXISTS subquery: {Render(tokens, start, end)}");

            ExpectWord(statement, tokens, ref position, close, "select");
            Require(statement, position < close && tokens[position].Kind == TokenKind.Number && tokens[position].Value == "1", "the EXISTS subquery must select 1");
            position++;
            ExpectWord(statement, tokens, ref position, close, "from");
            ExpectIdentifier(statement, tokens, ref position, close, "identity");
            Expect(statement, tokens, ref position, close, ".");
            ExpectIdentifier(statement, tokens, ref position, close, "users");

            if (position < close && tokens[position].IsWord("as"))
            {
                position++;
            }

            Require(statement, position < close && tokens[position].IsIdentifier, "the users table has no alias");
            var usersAlias = tokens[position].IdentifierName;
            Require(statement, !string.Equals(usersAlias, outerAlias, StringComparison.Ordinal), "the users alias shadows the notifications alias");
            position++;
            ExpectWord(statement, tokens, ref position, close, "where");

            var userIdPredicates = 0;
            var statusPredicates = 0;

            foreach (var (innerStart, innerEnd) in SplitConjuncts(statement, tokens, position, close))
            {
                var innerPosition = innerStart;

                // A bare column resolves to the innermost table (users); the outer alias is never accepted here.
                var column = ReadColumn(statement, tokens, ref innerPosition, innerEnd, allowedQualifier: usersAlias, rejectedQualifier: outerAlias);
                Expect(statement, tokens, ref innerPosition, innerEnd, "=");

                switch (column)
                {
                    case "id":
                        var userId = ReadElement(statement, tokens, ref innerPosition, innerEnd, ElementContext.Scalar);
                        ExpectEnd(statement, tokens, innerPosition, innerEnd);
                        Require(statement, userId.Count == 1 && userId[0] is Guid id && id == scope.RecipientUserId,
                            "the EXISTS user-id predicate is not bound to the recipient");
                        userIdPredicates++;
                        break;

                    case "status":
                        var status = ReadElement(statement, tokens, ref innerPosition, innerEnd, ElementContext.Scalar);
                        ExpectEnd(statement, tokens, innerPosition, innerEnd);
                        Require(
                            statement,
                            status.Count == 1
                                && (status[0] is UserStatus.Active
                                    || (status[0] is string text && string.Equals(text, "Active", StringComparison.Ordinal))),
                            $"the EXISTS status predicate is not exactly Active: {status.FirstOrDefault() ?? "null"}");
                        statusPredicates++;
                        break;

                    default:
                        throw Reject(statement, $"unsupported predicate inside EXISTS on '{column}': {Render(tokens, innerStart, innerEnd)}");
                }
            }

            Require(statement, userIdPredicates == 1 && statusPredicates == 1, "the EXISTS subquery must contain exactly the user-id and Active-status predicates");
        }

        private static (string OuterAlias, int WhereStart, int WhereEnd) LocateWhere(CommandEntry statement, Token[] tokens)
        {
            AssertBalanced(statement, tokens);
            Require(statement, tokens.Length > 0, "the statement is empty");

            int afterTable;
            string alias;

            if (tokens[0].IsWord("update"))
            {
                var position = 1;
                (alias, position) = ReadNotificationsTable(statement, tokens, position, terminator: "set");
                var where = FindTopLevelWord(statement, tokens, position, tokens.Length, "where");
                Require(statement, where > position && tokens[position].IsWord("set"), "the UPDATE has no SET clause before WHERE");

                // Nothing but the WHERE clause may follow; anything else is unsupported structure.
                foreach (var word in new[] { "from", "returning", "order", "limit", "offset", "group", "having", "union" })
                {
                    Require(statement, FindTopLevelWord(statement, tokens, where + 1, tokens.Length, word) < 0, $"unsupported UPDATE clause '{word}'");
                }

                return (alias, where + 1, tokens.Length);
            }

            Require(statement, tokens[0].IsWord("select"), "the statement is neither the known SELECT nor UPDATE shape");
            var from = FindTopLevelWord(statement, tokens, 1, tokens.Length, "from");
            Require(statement, from > 1, "the SELECT has no FROM clause");
            (alias, afterTable) = ReadNotificationsTable(statement, tokens, from + 1, terminator: "where");
            Require(statement, afterTable < tokens.Length && tokens[afterTable].IsWord("where"), "only a WHERE clause may follow the notifications table");

            var clauseStart = afterTable + 1;
            var clauseEnd = tokens.Length;

            foreach (var word in new[] { "order", "limit", "offset" })
            {
                var index = FindTopLevelWord(statement, tokens, clauseStart, tokens.Length, word);

                if (index >= 0 && index < clauseEnd)
                {
                    clauseEnd = index;
                }
            }

            AssertSelectTail(statement, tokens, clauseEnd);

            return (alias, clauseStart, clauseEnd);
        }

        // notifications.notifications [AS] alias; without an alias the table name itself is the binding.
        private static (string Alias, int Next) ReadNotificationsTable(CommandEntry statement, Token[] tokens, int position, string terminator)
        {
            ExpectIdentifier(statement, tokens, ref position, tokens.Length, "notifications");
            Expect(statement, tokens, ref position, tokens.Length, ".");
            ExpectIdentifier(statement, tokens, ref position, tokens.Length, "notifications");

            if (position < tokens.Length && tokens[position].IsWord("as"))
            {
                position++;
                Require(statement, position < tokens.Length && tokens[position].IsIdentifier, "AS is not followed by an alias");
            }

            if (position < tokens.Length && tokens[position].IsIdentifier && !tokens[position].IsWord(terminator))
            {
                return (tokens[position].IdentifierName, position + 1);
            }

            return ("notifications", position);
        }

        // ORDER BY <columns [ASC|DESC]> then optional LIMIT and OFFSET, each a parameter or a number.
        private static void AssertSelectTail(CommandEntry statement, Token[] tokens, int position)
        {
            if (position < tokens.Length && tokens[position].IsWord("order"))
            {
                position++;
                ExpectWord(statement, tokens, ref position, tokens.Length, "by");

                while (position < tokens.Length && !tokens[position].IsWord("limit") && !tokens[position].IsWord("offset"))
                {
                    var token = tokens[position];
                    Require(
                        statement,
                        token.IsIdentifier || token.IsSymbol(".") || token.IsSymbol(",") || token.IsWord("asc") || token.IsWord("desc")
                            || token.IsWord("nulls") || token.IsWord("first") || token.IsWord("last"),
                        $"unsupported ORDER BY token '{token.Raw}'");
                    position++;
                }
            }

            foreach (var word in new[] { "limit", "offset" })
            {
                if (position < tokens.Length && tokens[position].IsWord(word))
                {
                    position++;
                    Require(
                        statement,
                        position < tokens.Length && tokens[position].Kind is TokenKind.Parameter or TokenKind.Number,
                        $"{word} is not followed by a parameter or a number");
                    position++;
                }
            }

            Require(statement, position == tokens.Length, $"unsupported trailing SQL: {Render(tokens, position, tokens.Length)}");
        }

        // Splits on top-level AND. OR and NOT are rejected anywhere in the clause, at any depth. A parenthesized group
        // that is not a subquery is flattened; nothing is flattened across a subquery boundary.
        private static List<(int Start, int End)> SplitConjuncts(CommandEntry statement, Token[] tokens, int start, int end)
        {
            Require(statement, start < end, "an empty WHERE clause");

            for (var index = start; index < end; index++)
            {
                Require(statement, !tokens[index].IsWord("or") && !tokens[index].IsWord("not"), $"a permissive OR/NOT branch: {Render(tokens, start, end)}");
            }

            var conjuncts = new List<(int Start, int End)>();
            var depth = 0;
            var conjunctStart = start;

            for (var index = start; index < end; index++)
            {
                var token = tokens[index];

                if (token.IsSymbol("(") || token.IsSymbol("["))
                {
                    depth++;
                }
                else if (token.IsSymbol(")") || token.IsSymbol("]"))
                {
                    depth--;
                }
                else if (depth == 0 && token.IsWord("and"))
                {
                    AddConjunct(statement, tokens, conjunctStart, index, conjuncts);
                    conjunctStart = index + 1;
                }
            }

            AddConjunct(statement, tokens, conjunctStart, end, conjuncts);

            return conjuncts;
        }

        private static void AddConjunct(CommandEntry statement, Token[] tokens, int start, int end, List<(int Start, int End)> conjuncts)
        {
            Require(statement, start < end, "an empty conjunct");

            if (tokens[start].IsSymbol("(") && FindClose(statement, tokens, start, end) == end - 1
                && !(start + 1 < end && tokens[start + 1].IsWord("select")))
            {
                conjuncts.AddRange(SplitConjuncts(statement, tokens, start + 1, end - 1));

                return;
            }

            conjuncts.Add((start, end));
        }

        // A column reference: bare, or qualified by the one permitted alias. A qualifier naming the rejected alias, or
        // any other qualifier, is refused before the column is used.
        private static string ReadColumn(
            CommandEntry statement,
            Token[] tokens,
            ref int position,
            int end,
            string allowedQualifier,
            string? rejectedQualifier)
        {
            Require(statement, position < end && tokens[position].IsIdentifier, $"expected a column reference: {Render(tokens, position, end)}");

            if (position + 2 < end && tokens[position + 1].IsSymbol("."))
            {
                var qualifier = tokens[position].IdentifierName;
                Require(statement, tokens[position + 2].IsIdentifier, "a qualified column has no column name");
                Require(statement, rejectedQualifier is null || !string.Equals(qualifier, rejectedQualifier, StringComparison.Ordinal),
                    $"a column inside EXISTS references the outer alias '{qualifier}'");
                Require(statement, string.Equals(qualifier, allowedQualifier, StringComparison.Ordinal),
                    $"the column qualifier '{qualifier}' is not the bound alias '{allowedQualifier}'");
                var column = tokens[position + 2].IdentifierName;
                position += 3;

                return column;
            }

            var bare = tokens[position].IdentifierName;
            position++;

            return bare;
        }

        // = ANY ( @array [cast] ) | IN ( element {, element} ) | = element. Every token must be consumed.
        private static List<object?> ReadMembership(CommandEntry statement, Token[] tokens, int position, int end)
        {
            Require(statement, position < end, "a membership predicate has no operator");

            if (tokens[position].IsSymbol("=") && position + 1 < end && tokens[position + 1].IsWord("any"))
            {
                position += 2;
                Expect(statement, tokens, ref position, end, "(");
                var values = ReadElement(statement, tokens, ref position, end, ElementContext.Array);
                Expect(statement, tokens, ref position, end, ")");
                ExpectEnd(statement, tokens, position, end);

                return values;
            }

            if (tokens[position].IsWord("in"))
            {
                position++;
                Expect(statement, tokens, ref position, end, "(");
                var values = new List<object?>();

                while (true)
                {
                    values.AddRange(ReadElement(statement, tokens, ref position, end, ElementContext.Scalar));

                    if (position < end && tokens[position].IsSymbol(","))
                    {
                        position++;
                        continue;
                    }

                    break;
                }

                Expect(statement, tokens, ref position, end, ")");
                ExpectEnd(statement, tokens, position, end);

                return values;
            }

            return ReadSingle(statement, tokens, position, end);
        }

        private static List<object?> ReadSingle(CommandEntry statement, Token[] tokens, int position, int end)
        {
            Expect(statement, tokens, ref position, end, "=");
            var values = ReadElement(statement, tokens, ref position, end, ElementContext.Scalar);
            ExpectEnd(statement, tokens, position, end);

            return values;
        }

        // One parameter, literal or number, with an optional allowlisted cast. Null, empty and unsupported values fail.
        private static List<object?> ReadElement(CommandEntry statement, Token[] tokens, ref int position, int end, ElementContext context)
        {
            Require(statement, position < end, "a predicate is missing its value");
            var token = tokens[position];
            position++;
            List<object?> values;

            switch (token.Kind)
            {
                case TokenKind.Parameter:
                    var value = ParameterValue(statement, token.Value);

                    if (context == ElementContext.Array)
                    {
                        Require(statement, value is object?[], $"parameter @{token.Value} is not an array");
                        values = [.. (object?[])value!];
                        Require(statement, values.Count > 0, $"parameter @{token.Value} is an empty membership");
                    }
                    else
                    {
                        Require(statement, value is not object?[], $"parameter @{token.Value} is an array where a single value is required");
                        values = [value];
                    }

                    break;

                case TokenKind.Literal when context == ElementContext.Scalar:
                    values = [token.Value];
                    break;

                case TokenKind.Number when context == ElementContext.Scalar:
                    values = [long.Parse(token.Value, System.Globalization.CultureInfo.InvariantCulture)];
                    break;

                default:
                    throw Reject(statement, $"unsupported membership element '{token.Raw}'");
            }

            Require(statement, values.All(element => element is not null), "a membership element is null");
            SkipCast(statement, tokens, ref position, end);

            return values;
        }

        // :: text | character varying | smallint | integer | bigint | uuid, optionally followed by [].
        private static void SkipCast(CommandEntry statement, Token[] tokens, ref int position, int end)
        {
            if (position >= end || !tokens[position].IsSymbol("::"))
            {
                return;
            }

            position++;
            Require(statement, position < end && tokens[position].Kind == TokenKind.Word, "a cast has no type");
            var type = tokens[position].Value.ToLowerInvariant();
            position++;

            if (type == "character")
            {
                Require(statement, position < end && tokens[position].IsWord("varying"), "unsupported cast 'character'");
                position++;
                type = "character varying";
            }

            Require(statement, type is "text" or "character varying" or "smallint" or "integer" or "bigint" or "uuid", $"unsupported cast '{type}'");

            if (position < end && tokens[position].IsSymbol("["))
            {
                position++;
                Expect(statement, tokens, ref position, end, "]");
            }
        }

        private static long ToKind(CommandEntry statement, object? value)
        {
            return value switch
            {
                NotificationVisibilityKind kind => (long)kind,
                short number => number,
                int number => number,
                long number => number,
                byte number => number,
                _ => throw Reject(statement, $"a visibility-kind element is not an integral value: {value ?? "null"}")
            };
        }

        // Duplicates cannot widen SQL membership, so set semantics apply, but every element must be expected and the
        // distinct set must equal the expected set exactly.
        private static void AssertSetEquals<T>(CommandEntry statement, string what, List<T> actual, HashSet<T> expected)
        {
            Require(statement, actual.Count > 0, $"empty {what} membership");
            var unexpected = actual.Where(value => !expected.Contains(value)).ToList();
            Require(statement, unexpected.Count == 0, $"unexpected {what}: {string.Join(", ", unexpected)}");
            Require(statement, expected.SetEquals(actual), $"{what} [{string.Join(", ", actual)}] differ from [{string.Join(", ", expected)}]");
        }

        private static object? ParameterValue(CommandEntry statement, string name)
        {
            var parameter = statement.Parameters.FirstOrDefault(
                candidate => string.Equals(candidate.Name.TrimStart('@', ':'), name, StringComparison.OrdinalIgnoreCase));

            Require(statement, parameter is not null, $"parameter @{name} was not sent");
            Require(statement, parameter!.Error is null, $"parameter @{name} could not be snapshotted: {parameter.Error}");

            return parameter.Value;
        }

        private static Token[] Tokenize(CommandEntry statement)
        {
            var sql = statement.Text;
            var tokens = new List<Token>();
            var index = 0;

            while (index < sql.Length)
            {
                var character = sql[index];

                if (char.IsWhiteSpace(character))
                {
                    index++;
                    continue;
                }

                var next = index + 1 < sql.Length ? sql[index + 1] : '\0';
                Require(statement, !(character == '-' && next == '-') && !(character == '/' && next == '*'), "SQL comments are not supported");
                Require(statement, character != ';', "multiple statements are not supported");

                if (character is '\'' or '"')
                {
                    var start = index;
                    var value = new StringBuilder();
                    index++;

                    while (true)
                    {
                        Require(statement, index < sql.Length, "an unterminated quoted token");

                        if (sql[index] == character)
                        {
                            if (index + 1 < sql.Length && sql[index + 1] == character)
                            {
                                value.Append(character);
                                index += 2;
                                continue;
                            }

                            index++;
                            break;
                        }

                        value.Append(sql[index]);
                        index++;
                    }

                    var raw = sql[start..index];
                    var decoded = value.ToString();

                    if (character == '\'')
                    {
                        tokens.Add(new Token(TokenKind.Literal, raw, decoded));
                    }
                    else
                    {
                        Require(statement, KnownQuotedIdentifiers.Contains(decoded) || AliasShape.IsMatch(decoded),
                            $"unsupported quoted identifier {raw}");
                        tokens.Add(new Token(TokenKind.QuotedIdentifier, raw, decoded));
                    }

                    continue;
                }

                if (character == '@')
                {
                    var start = index;
                    index++;
                    Require(statement, index < sql.Length && (char.IsAsciiLetter(sql[index]) || sql[index] == '_'), "a malformed parameter placeholder");

                    while (index < sql.Length && (char.IsAsciiLetterOrDigit(sql[index]) || sql[index] == '_'))
                    {
                        index++;
                    }

                    tokens.Add(new Token(TokenKind.Parameter, sql[start..index], sql[(start + 1)..index]));
                    continue;
                }

                if (char.IsAsciiDigit(character))
                {
                    var start = index;

                    while (index < sql.Length && char.IsAsciiDigit(sql[index]))
                    {
                        index++;
                    }

                    tokens.Add(new Token(TokenKind.Number, sql[start..index], sql[start..index]));
                    continue;
                }

                if (char.IsAsciiLetter(character) || character == '_')
                {
                    var start = index;

                    while (index < sql.Length && (char.IsAsciiLetterOrDigit(sql[index]) || sql[index] == '_'))
                    {
                        index++;
                    }

                    tokens.Add(new Token(TokenKind.Word, sql[start..index], sql[start..index]));
                    continue;
                }

                if (character == ':' && next == ':')
                {
                    tokens.Add(new Token(TokenKind.Punctuation, "::", "::"));
                    index += 2;
                    continue;
                }

                Require(statement, "(),.=<>*[]".Contains(character), $"unsupported character '{character}'");
                tokens.Add(new Token(TokenKind.Punctuation, character.ToString(), character.ToString()));
                index++;
            }

            return [.. tokens];
        }

        private static void AssertBalanced(CommandEntry statement, Token[] tokens)
        {
            var delimiters = new Stack<string>();

            foreach (var token in tokens)
            {
                if (token.IsSymbol("(") || token.IsSymbol("["))
                {
                    delimiters.Push(token.Value);
                }
                else if (token.IsSymbol(")") || token.IsSymbol("]"))
                {
                    Require(statement, delimiters.Count > 0, $"an unbalanced '{token.Value}'");
                    var open = delimiters.Pop();
                    Require(statement, (open == "(" && token.Value == ")") || (open == "[" && token.Value == "]"), "mismatched delimiters");
                }
            }

            Require(statement, delimiters.Count == 0, "unclosed delimiters");
        }

        private static int FindClose(CommandEntry statement, Token[] tokens, int open, int end)
        {
            var depth = 0;

            for (var index = open; index < end; index++)
            {
                if (tokens[index].IsSymbol("("))
                {
                    depth++;
                }
                else if (tokens[index].IsSymbol(")") && --depth == 0)
                {
                    return index;
                }
            }

            throw Reject(statement, "an unclosed parenthesis");
        }

        private static int FindTopLevelWord(CommandEntry statement, Token[] tokens, int start, int end, string word)
        {
            var depth = 0;

            for (var index = start; index < end; index++)
            {
                if (tokens[index].IsSymbol("(") || tokens[index].IsSymbol("["))
                {
                    depth++;
                }
                else if (tokens[index].IsSymbol(")") || tokens[index].IsSymbol("]"))
                {
                    depth--;
                    Require(statement, depth >= 0, "an unbalanced closing delimiter");
                }
                else if (depth == 0 && tokens[index].IsWord(word))
                {
                    return index;
                }
            }

            return -1;
        }

        private static void Expect(CommandEntry statement, Token[] tokens, ref int position, int end, string symbol)
        {
            Require(statement, position < end && tokens[position].IsSymbol(symbol), $"expected '{symbol}' at: {Render(tokens, position, end)}");
            position++;
        }

        private static void ExpectWord(CommandEntry statement, Token[] tokens, ref int position, int end, string word)
        {
            Require(statement, position < end && tokens[position].IsWord(word), $"expected '{word}' at: {Render(tokens, position, end)}");
            position++;
        }

        private static void ExpectIdentifier(CommandEntry statement, Token[] tokens, ref int position, int end, string name)
        {
            Require(statement, position < end && tokens[position].IsIdentifier && tokens[position].IdentifierName == name,
                $"expected identifier '{name}' at: {Render(tokens, position, end)}");
            position++;
        }

        private static void ExpectEnd(CommandEntry statement, Token[] tokens, int position, int end)
        {
            Require(statement, position == end, $"unexpected trailing tokens: {Render(tokens, position, end)}");
        }

        private static string Render(Token[] tokens, int start, int end)
        {
            return start >= end ? "<end>" : string.Join(" ", tokens[start..Math.Min(end, tokens.Length)].Select(token => token.Raw));
        }

        private static void Require(CommandEntry statement, bool condition, string reason)
        {
            if (!condition)
            {
                throw Reject(statement, reason);
            }
        }

        private static Xunit.Sdk.XunitException Reject(CommandEntry statement, string reason)
        {
            return new Xunit.Sdk.XunitException(
                $"R17 SQL recognizer: {reason}.{Environment.NewLine}Statement:{Environment.NewLine}{statement.Text}");
        }
    }
}
