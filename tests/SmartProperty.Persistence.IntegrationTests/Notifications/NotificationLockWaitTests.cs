using System.Data;
using System.Data.Common;
using System.Diagnostics;
using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using SmartProperty.Application.Abstractions.Persistence.Repositories;
using SmartProperty.Application.Notifications;
using SmartProperty.Domain.Notifications;
using SmartProperty.Persistence.Context;
using SmartProperty.Persistence.IntegrationTests.Infrastructure;
using Xunit;

namespace SmartProperty.Persistence.IntegrationTests.Notifications;

/// <summary>
/// C6 Option A against real PostgreSQL row locks (C6-1 to C6-5): an UPDATE that began while the recipient was Active
/// and then waited on a row lock completes even though the recipient's suspension committed during that wait, and
/// every statement that starts after the suspension is visible grants nothing and changes nothing.
/// </summary>
/// <remarks>
/// <para>
/// The wait is proved by the server, never by a delay: the caller's backend PID is captured from the connection that
/// carries its UPDATE, and the suspension is only committed once <c>pg_stat_activity</c> reports that backend waiting
/// on a lock and <c>pg_blocking_pids</c> names the holder's backend. The same evidence is read again after the
/// suspension has committed, so the order "statement started, then waited, then the suspension committed" is
/// observed, not assumed. Polling sleeps only between observations.
/// </para>
/// <para>
/// The holder, the status writer and the monitor use their own unpooled connections with a server-side
/// <c>lock_timeout</c>, so none of them can wait indefinitely, and closing the holder's connection always releases
/// its locks. The caller is the real repository from the test host, bounded by a cancellation deadline; cleanup
/// releases the holder first and only then waits, briefly, for the caller.
/// </para>
/// </remarks>
public sealed class NotificationLockWaitTests(PostgreSqlFixture fixture) : DatabaseTest(fixture)
{
    private static readonly Func<Guid, NotificationVisibilityScope> CreateScope = BindScopeFactory();

    // Whole seconds after the fixed test clock, so every value is already at storage precision.
    private static readonly DateTimeOffset CreatedAt = TestClock.DefaultNow.AddMinutes(1);
    private static readonly DateTimeOffset HolderReadAt = CreatedAt.AddMinutes(3);
    private static readonly DateTimeOffset ReadAt = CreatedAt.AddMinutes(5);
    private static readonly DateTimeOffset LaterReadAt = CreatedAt.AddMinutes(10);
    private static readonly DateTimeOffset StatusChangedAt = TestClock.DefaultNow.AddHours(1);

    private static readonly TimeSpan ScenarioDeadline = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan DiscoveryDeadline = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan CompletionDeadline = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan CleanupDeadline = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(20);

    // Every side connection: it never waits longer than lock_timeout for a lock, no statement outlives
    // statement_timeout, and an abandoned open transaction is ended by the server.
    private const string SideConnectionOptions =
        "-c lock_timeout=5000 -c statement_timeout=20000 -c idle_in_transaction_session_timeout=90000";

    private readonly NotificationUpdateWatcher updates = new();

    private protected override IInterceptor[] ExtraInterceptors()
    {
        return [updates];
    }

    // C6-1
    [Fact]
    public async Task C6_1_AMarkReadWaitingOnARowLockCompletesAfterTheRecipientIsSuspendedMeanwhile()
    {
        var recipient = await SeedUserAsync();
        var target = await SeedNotificationAsync(recipient, CreatedAt);
        var untouched = await SeedNotificationAsync(recipient, CreatedAt.AddSeconds(1));

        // Issued while the account is Active, exactly as the gate would have issued it.
        var scope = CreateScope(recipient);

        await using var session = OpenSession();
        var run = await RunThroughLockWaitAsync(
            session,
            recipient,
            target,
            HolderAction.LockOnly,
            (repository, token) => repository.MarkReadAsync(scope, target, ReadAt, token));

        Assert.Equal(NotificationReadOutcome.MarkedRead, run.Result);

        // The waiting UPDATE itself changed the row: one statement, no probe, no retry.
        Assert.Equal(1, run.CommandCount);
        Assert.Equal(1, run.UpdateCount);
        Assert.Equal(ReadAt, await ReadAtAsync(target));
        Assert.Null(await ReadAtAsync(untouched));
        Assert.Equal("Suspended", await UserStatusAsync(recipient));
    }

    // C6-2
    [Fact]
    public async Task C6_2_AMarkReadRecheckedAgainstTheHoldersNewRowVersionStillCompletes()
    {
        var recipient = await SeedUserAsync();
        var target = await SeedNotificationAsync(recipient, CreatedAt);
        var scope = CreateScope(recipient);

        // The holder writes a new version of the row and commits it, so PostgreSQL re-evaluates the waiting UPDATE's
        // WHERE clause against that version (EvalPlanQual). The recipient's eligibility is not re-read by that check.
        await using var session = OpenSession();
        var run = await RunThroughLockWaitAsync(
            session,
            recipient,
            target,
            HolderAction.RewriteUnchanged,
            (repository, token) => repository.MarkReadAsync(scope, target, ReadAt, token));

        Assert.Equal(NotificationReadOutcome.MarkedRead, run.Result);
        Assert.Equal(1, run.CommandCount);
        Assert.Equal(1, run.UpdateCount);
        Assert.Equal(ReadAt, await ReadAtAsync(target));
        Assert.Equal("Suspended", await UserStatusAsync(recipient));
    }

    // C6-3
    [Fact]
    public async Task C6_3_ABulkMarkWaitingOnOneRowLockCompletesForEveryRowAfterTheSuspension()
    {
        var recipient = await SeedUserAsync();
        var free = await SeedNotificationAsync(recipient, CreatedAt);
        var locked = await SeedNotificationAsync(recipient, CreatedAt.AddSeconds(1));
        var notDisplayed = await SeedNotificationAsync(recipient, CreatedAt.AddSeconds(2));
        var scope = CreateScope(recipient);

        await using var session = OpenSession();
        var run = await RunThroughLockWaitAsync(
            session,
            recipient,
            locked,
            HolderAction.LockOnly,
            (repository, token) => repository.MarkDisplayedReadAsync(scope, [free, locked], ReadAt, token));

        Assert.Equal(2, run.Result);
        Assert.Equal(1, run.CommandCount);
        Assert.Equal(1, run.UpdateCount);
        Assert.Equal(ReadAt, await ReadAtAsync(free));
        Assert.Equal(ReadAt, await ReadAtAsync(locked));
        Assert.Null(await ReadAtAsync(notDisplayed));
        Assert.Equal("Suspended", await UserStatusAsync(recipient));
    }

    // C6-4
    [Fact]
    public async Task C6_4_EveryLaterStatementSeesTheSuspensionAndReturnsOrChangesNothing()
    {
        var recipient = await SeedUserAsync();
        var target = await SeedNotificationAsync(recipient, CreatedAt);
        var other = await SeedNotificationAsync(recipient, CreatedAt.AddSeconds(1));
        var scope = CreateScope(recipient);

        await using var session = OpenSession();
        var run = await RunThroughLockWaitAsync(
            session,
            recipient,
            target,
            HolderAction.LockOnly,
            (repository, token) => repository.MarkReadAsync(scope, target, ReadAt, token));

        // Arrangement only (C6-1 owns this assertion): the in-flight statement completed.
        Assert.Equal(NotificationReadOutcome.MarkedRead, run.Result);

        var targetBefore = await RowSnapshotAsync(target);
        var otherBefore = await RowSnapshotAsync(other);
        var repository = session.Repository;

        // The same stale scope and the same session: every later statement sees the committed suspension.
        Host.Commands.Reset();
        var page = await repository.ListAsync(scope, false, 0, 10);
        Assert.Empty(page.Items);
        Assert.Equal(0L, page.TotalCount);
        var unreadPage = await repository.ListAsync(scope, true, 0, 10);
        Assert.Empty(unreadPage.Items);
        Assert.Equal(0L, unreadPage.TotalCount);
        Assert.Equal(4, Host.Commands.Count);

        Host.Commands.Reset();
        Assert.Equal(0, await repository.CountUnreadAsync(scope));
        Assert.Equal(1, Host.Commands.Count);

        // The row this scope marked read a moment ago is NotFound, not AlreadyRead: its existence is not disclosed.
        Host.Commands.Reset();
        Assert.Equal(NotificationReadOutcome.NotFound, await repository.MarkReadAsync(scope, target, LaterReadAt));
        Assert.Equal(2, Host.Commands.Count);

        Host.Commands.Reset();
        Assert.Equal(NotificationReadOutcome.NotFound, await repository.MarkReadAsync(scope, other, LaterReadAt));
        Assert.Equal(2, Host.Commands.Count);

        Host.Commands.Reset();
        Assert.Equal(0, await repository.MarkDisplayedReadAsync(scope, [target, other], LaterReadAt));
        Assert.Equal(1, Host.Commands.Count);

        // Nothing persisted changed.
        Assert.Equal(targetBefore, await RowSnapshotAsync(target));
        Assert.Equal(otherBefore, await RowSnapshotAsync(other));
        Assert.Equal(ReadAt, await ReadAtAsync(target));
        Assert.Null(await ReadAtAsync(other));

        // Control: the rows were there throughout and only eligibility hid them.
        await SetUserStatusAsync(recipient, "Active");
        var restored = await repository.ListAsync(scope, false, 0, 10);
        Assert.Equal(2L, restored.TotalCount);
        Assert.Equal(new[] { other, target }, restored.Items.Select(item => item.Id));
        Assert.Equal(1, await repository.CountUnreadAsync(scope));
    }

    // C6-5
    [Fact]
    public async Task C6_5_AWaitingMarkReadThatMatchesNothingAfterTheHoldersReadProbesAfterTheSuspensionAsNotFound()
    {
        var recipient = await SeedUserAsync();
        var target = await SeedNotificationAsync(recipient, CreatedAt);
        var scope = CreateScope(recipient);

        // The holder marks the row read and commits after the suspension. Rechecked against that version, the waiting
        // UPDATE matches nothing; the probe that follows is a new statement, so it sees the suspension.
        await using var session = OpenSession();
        var run = await RunThroughLockWaitAsync(
            session,
            recipient,
            target,
            HolderAction.MarkReadFirst,
            (repository, token) => repository.MarkReadAsync(scope, target, ReadAt, token));

        // Not AlreadyRead: the probe ran after the suspension became visible and saw no row.
        Assert.Equal(NotificationReadOutcome.NotFound, run.Result);

        // The update and one probe; the outcome was decided without a retry.
        Assert.Equal(2, run.CommandCount);
        Assert.Equal(1, run.UpdateCount);

        // The holder's first read time stands.
        Assert.Equal(HolderReadAt, await ReadAtAsync(target));
        Assert.Equal("Suspended", await UserStatusAsync(recipient));
    }

    /// <summary>
    /// Runs <paramref name="operation"/> through a verified row-lock wait: the holder takes the row, the caller's
    /// UPDATE is seen waiting on the holder, the recipient is suspended and that commit is confirmed while the caller
    /// is still waiting, and only then is the holder released.
    /// </summary>
    private async Task<LockWaitRun<T>> RunThroughLockWaitAsync<T>(
        Session session,
        Guid recipient,
        Guid lockedId,
        HolderAction action,
        Func<INotificationRepository, CancellationToken, Task<T>> operation)
    {
        using var deadline = new CancellationTokenSource(ScenarioDeadline);

        await AssertReadCommittedAsync(session.Context, deadline.Token);
        Assert.Equal("Active", await UserStatusAsync(recipient));

        var holder = await LockHolder.BeginAsync(SideConnectionString("c6-holder"), deadline.Token);
        Task<T>? call = null;

        try
        {
            await holder.TakeAsync(action, lockedId, deadline.Token);

            var firstUpdate = updates.Arm();
            Host.Commands.Reset();
            call = Task.Run(() => operation(session.Repository, deadline.Token));

            var update = await WaitForFirstUpdateAsync(firstUpdate, call);

            // No explicit transaction: the statement runs on its own at the server default, READ COMMITTED.
            Assert.True(update.RanWithoutTransaction, "The repository's UPDATE ran inside an explicit transaction.");

            var waiting = await WaitUntilWaitingOnHolderAsync(update.ProcessId, holder.ProcessId, call, deadline.Token);
            Assert.True(waiting.WaitEvent is "transactionid" or "tuple", $"Not a row-lock wait: {waiting}.");

            await SetUserStatusAsync(recipient, "Suspended");
            Assert.Equal("Suspended", await UserStatusAsync(recipient));

            // The suspension is committed and visible, and the caller's statement is still waiting on the holder.
            var stillWaiting = await ObserveOnceAsync(update.ProcessId, holder.ProcessId, deadline.Token);
            Assert.True(stillWaiting.IsWaitingOnHolder, $"The caller stopped waiting before the release: {stillWaiting}.");
            Assert.False(call.IsCompleted, "The caller's operation completed before the holder released the row.");

            await holder.ReleaseAsync(deadline.Token);

            var result = await call.WaitAsync(CompletionDeadline, deadline.Token);

            return new LockWaitRun<T>(result, Host.Commands.Count, updates.UpdateCount, waiting);
        }
        finally
        {
            // The holder first: rolling back and closing its unpooled connection releases every lock it holds, so a
            // caller still waiting can finish. Only then is the caller cancelled and awaited, for a bounded time.
            await holder.DisposeAsync();

            if (call is { IsCompleted: false })
            {
                await deadline.CancelAsync();

                try
                {
                    await call.WaitAsync(CleanupDeadline);
                }
                catch (Exception)
                {
                    // Cleanup only. The failure that brought the test here is the one reported.
                }
            }
        }
    }

    private static async Task<UpdateObservation> WaitForFirstUpdateAsync(Task<UpdateObservation> firstUpdate, Task call)
    {
        var finished = await Task.WhenAny(firstUpdate, call, Task.Delay(DiscoveryDeadline));

        if (finished == firstUpdate)
        {
            return await firstUpdate;
        }

        if (finished == call)
        {
            throw new InvalidOperationException(
                $"The operation ended ({call.Status}) before it issued its UPDATE. {call.Exception?.GetBaseException().Message}");
        }

        throw new TimeoutException($"The operation did not issue its UPDATE within {DiscoveryDeadline}.");
    }

    private async Task<LockWaitObservation> WaitUntilWaitingOnHolderAsync(
        int callerProcessId,
        int holderProcessId,
        Task call,
        CancellationToken cancellationToken)
    {
        await using var monitor = new NpgsqlConnection(SideConnectionString("c6-monitor"));
        await monitor.OpenAsync(cancellationToken);

        var clock = Stopwatch.StartNew();
        LockWaitObservation? last = null;

        while (clock.Elapsed < DiscoveryDeadline)
        {
            if (call.IsCompleted)
            {
                throw new InvalidOperationException(
                    $"The operation ended ({call.Status}) before PostgreSQL showed it waiting on the holder. "
                    + $"Last observation: {last}. {call.Exception?.GetBaseException().Message}");
            }

            last = await ObserveAsync(monitor, callerProcessId, holderProcessId, cancellationToken);

            if (last.IsWaitingOnHolder)
            {
                return last;
            }

            // Only the interval between observations; the observation itself is the evidence.
            await Task.Delay(PollInterval, cancellationToken);
        }

        throw new TimeoutException(
            $"PostgreSQL did not show backend {callerProcessId} waiting on holder {holderProcessId} within "
            + $"{DiscoveryDeadline}. Last observation: {last}.");
    }

    private async Task<LockWaitObservation> ObserveOnceAsync(
        int callerProcessId,
        int holderProcessId,
        CancellationToken cancellationToken)
    {
        await using var monitor = new NpgsqlConnection(SideConnectionString("c6-monitor"));
        await monitor.OpenAsync(cancellationToken);

        return await ObserveAsync(monitor, callerProcessId, holderProcessId, cancellationToken);
    }

    private static async Task<LockWaitObservation> ObserveAsync(
        NpgsqlConnection monitor,
        int callerProcessId,
        int holderProcessId,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            SELECT a.state, a.wait_event_type, a.wait_event, @holder = ANY (pg_blocking_pids(a.pid))
            FROM pg_stat_activity AS a
            WHERE a.pid = @caller
            """,
            monitor);
        command.Parameters.AddWithValue("caller", callerProcessId);
        command.Parameters.AddWithValue("holder", holderProcessId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        if (!await reader.ReadAsync(cancellationToken))
        {
            return new LockWaitObservation(callerProcessId, holderProcessId, "no such backend", null, null, false);
        }

        return new LockWaitObservation(
            callerProcessId,
            holderProcessId,
            reader.IsDBNull(0) ? null : reader.GetString(0),
            reader.IsDBNull(1) ? null : reader.GetString(1),
            reader.IsDBNull(2) ? null : reader.GetString(2),
            !reader.IsDBNull(3) && reader.GetBoolean(3));
    }

    private static async Task AssertReadCommittedAsync(ApplicationDbContext context, CancellationToken cancellationToken)
    {
        var isolation = await context.Database
            .SqlQueryRaw<string>(
                "SELECT current_setting('transaction_isolation') || '|' || current_setting('default_transaction_isolation') AS \"Value\"")
            .SingleAsync(cancellationToken);

        Assert.Equal("read committed|read committed", isolation);
    }

    private Session OpenSession()
    {
        return new Session(Host.CreateScope());
    }

    private string SideConnectionString(string applicationName)
    {
        return new NpgsqlConnectionStringBuilder(Host.ConnectionString)
        {
            ApplicationName = applicationName,
            Pooling = false,
            Options = SideConnectionOptions
        }.ConnectionString;
    }

    private async Task<Guid> SeedUserAsync()
    {
        var user = AccessGraph.ActiveUser($"lockwait-{Guid.NewGuid():n}@example.test");

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

    private async Task SetUserStatusAsync(Guid userId, string status)
    {
        await using var connection = new NpgsqlConnection(SideConnectionString("c6-status-writer"));
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "UPDATE identity.users SET status = @status, updated_at = @updatedAt WHERE id = @id",
            connection);
        command.Parameters.AddWithValue("status", status);
        command.Parameters.AddWithValue("updatedAt", StatusChangedAt);
        command.Parameters.AddWithValue("id", userId);

        Assert.Equal(1, await command.ExecuteNonQueryAsync());
    }

    private Task<string> UserStatusAsync(Guid userId)
    {
        return ScalarAsync<string>("SELECT status FROM identity.users WHERE id = @id", userId);
    }

    private Task<string> RowSnapshotAsync(Guid id)
    {
        return ScalarAsync<string>("SELECT row_to_json(n)::text FROM notifications.notifications AS n WHERE n.id = @id", id);
    }

    private async Task<DateTimeOffset?> ReadAtAsync(Guid id)
    {
        await using var connection = new NpgsqlConnection(SideConnectionString("c6-verify"));
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("SELECT read_at FROM notifications.notifications WHERE id = @id", connection);
        command.Parameters.AddWithValue("id", id);
        await using var reader = await command.ExecuteReaderAsync();

        Assert.True(await reader.ReadAsync(), $"Notification {id} does not exist.");

        return reader.IsDBNull(0) ? null : reader.GetFieldValue<DateTimeOffset>(0);
    }

    private async Task<T> ScalarAsync<T>(string sql, Guid id)
    {
        await using var connection = new NpgsqlConnection(SideConnectionString("c6-verify"));
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("id", id);
        await using var reader = await command.ExecuteReaderAsync();

        Assert.True(await reader.ReadAsync(), $"No row returned by: {sql}");

        return reader.GetFieldValue<T>(0);
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

    /// <summary>What the holder does to the row while the caller waits, and how it lets go.</summary>
    private sealed record HolderAction(string Description, string Sql, bool Writes)
    {
        /// <summary>Locks the row without changing it, then rolls back.</summary>
        public static readonly HolderAction LockOnly = new(
            "lock the row FOR UPDATE, then roll back",
            "SELECT id FROM notifications.notifications WHERE id = @id FOR UPDATE",
            Writes: false);

        /// <summary>Writes a new, identical version of the row, then commits it.</summary>
        public static readonly HolderAction RewriteUnchanged = new(
            "rewrite the row unchanged, then commit",
            "UPDATE notifications.notifications SET read_at = read_at WHERE id = @id",
            Writes: true);

        /// <summary>Marks the row read first, then commits.</summary>
        public static readonly HolderAction MarkReadFirst = new(
            "mark the row read, then commit",
            "UPDATE notifications.notifications SET read_at = @holderReadAt WHERE id = @id AND read_at IS NULL",
            Writes: true);
    }

    /// <summary>
    /// An independent transaction that holds a row lock until it is released. Its connection is unpooled, so
    /// disposing it always ends the backend and with it every lock.
    /// </summary>
    private sealed class LockHolder : IAsyncDisposable
    {
        private readonly NpgsqlConnection connection;
        private NpgsqlTransaction? transaction;
        private HolderAction? action;

        private LockHolder(NpgsqlConnection connection, NpgsqlTransaction transaction)
        {
            this.connection = connection;
            this.transaction = transaction;
        }

        public int ProcessId => connection.ProcessID;

        public static async Task<LockHolder> BeginAsync(string connectionString, CancellationToken cancellationToken)
        {
            var connection = new NpgsqlConnection(connectionString);

            try
            {
                await connection.OpenAsync(cancellationToken);
                var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);

                return new LockHolder(connection, transaction);
            }
            catch
            {
                await connection.DisposeAsync();
                throw;
            }
        }

        public async Task TakeAsync(HolderAction holderAction, Guid id, CancellationToken cancellationToken)
        {
            var current = transaction ?? throw new InvalidOperationException("The holder has already let go.");
            action = holderAction;

            await using var command = new NpgsqlCommand(holderAction.Sql, connection, current);
            command.Parameters.AddWithValue("id", id);

            if (holderAction.Sql.Contains("@holderReadAt", StringComparison.Ordinal))
            {
                command.Parameters.AddWithValue("holderReadAt", HolderReadAt);
            }

            if (!holderAction.Writes)
            {
                Assert.Equal<object?>(id, await command.ExecuteScalarAsync(cancellationToken));
                return;
            }

            Assert.Equal(1, await command.ExecuteNonQueryAsync(cancellationToken));

            // The holder's transaction now owns a new version of the row, which is what forces the waiting UPDATE to
            // re-check its WHERE clause against that version once the holder commits.
            await using var version = new NpgsqlCommand(
                "SELECT xmin::text::bigint = pg_current_xact_id()::text::bigint % 4294967296 FROM notifications.notifications WHERE id = @id",
                connection,
                current);
            version.Parameters.AddWithValue("id", id);
            Assert.True(
                await version.ExecuteScalarAsync(cancellationToken) is true,
                "The holder's transaction does not own the current version of the row.");
        }

        /// <summary>Commits what the holder wrote, or rolls back a lock-only hold.</summary>
        public async Task ReleaseAsync(CancellationToken cancellationToken)
        {
            var current = transaction ?? throw new InvalidOperationException("The holder has already let go.");
            transaction = null;

            if (action is { Writes: true })
            {
                await current.CommitAsync(cancellationToken);
            }
            else
            {
                await current.RollbackAsync(cancellationToken);
            }

            await current.DisposeAsync();
        }

        public async ValueTask DisposeAsync()
        {
            var current = transaction;
            transaction = null;

            if (current is not null)
            {
                try
                {
                    await current.RollbackAsync();
                }
                catch (Exception)
                {
                    // Closing the unpooled connection below ends the backend, which releases the locks regardless.
                }

                await current.DisposeAsync();
            }

            await connection.DisposeAsync();
        }
    }

    /// <summary>
    /// Observes the repository's UPDATE commands on notifications: the backend that runs the first one after
    /// <see cref="Arm"/>, whether it ran inside an explicit transaction, and how many ran. It never changes a command.
    /// </summary>
    private sealed class NotificationUpdateWatcher : DbCommandInterceptor
    {
        private readonly Lock sync = new();
        private TaskCompletionSource<UpdateObservation>? armed;
        private int updateCount;

        public int UpdateCount => Volatile.Read(ref updateCount);

        public Task<UpdateObservation> Arm()
        {
            lock (sync)
            {
                Interlocked.Exchange(ref updateCount, 0);
                armed = new TaskCompletionSource<UpdateObservation>(TaskCreationOptions.RunContinuationsAsynchronously);

                return armed.Task;
            }
        }

        public override InterceptionResult<int> NonQueryExecuting(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<int> result)
        {
            Observe(command);

            return base.NonQueryExecuting(command, eventData, result);
        }

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            Observe(command);

            return base.NonQueryExecutingAsync(command, eventData, result, cancellationToken);
        }

        private void Observe(DbCommand command)
        {
            var text = command.CommandText.TrimStart();

            if (!text.StartsWith("UPDATE", StringComparison.OrdinalIgnoreCase)
                || !text.Contains("notifications", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            Interlocked.Increment(ref updateCount);

            TaskCompletionSource<UpdateObservation>? target;

            lock (sync)
            {
                target = armed;
                armed = null;
            }

            if (target is null)
            {
                return;
            }

            if (command.Connection is NpgsqlConnection connection)
            {
                target.TrySetResult(new UpdateObservation(connection.ProcessID, command.Transaction is null));
            }
            else
            {
                target.TrySetException(new InvalidOperationException("The notification UPDATE did not run on an NpgsqlConnection."));
            }
        }
    }

    private sealed record UpdateObservation(int ProcessId, bool RanWithoutTransaction);

    private sealed record LockWaitObservation(
        int CallerProcessId,
        int HolderProcessId,
        string? State,
        string? WaitEventType,
        string? WaitEvent,
        bool BlockedByHolder)
    {
        public bool IsWaitingOnHolder => BlockedByHolder && WaitEventType == "Lock";
    }

    private sealed record LockWaitRun<T>(T Result, int CommandCount, int UpdateCount, LockWaitObservation Wait);

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
}
