using System.Collections.Concurrent;
using System.Data.Common;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using SmartProperty.Application.Abstractions.Persistence.Repositories;
using SmartProperty.Application.Notifications;
using SmartProperty.Domain.Notifications;
using SmartProperty.Persistence.IntegrationTests.Infrastructure;
using Xunit;

namespace SmartProperty.Persistence.IntegrationTests.Notifications;

/// <summary>
/// The real <see cref="INotificationRepository"/> under real PostgreSQL contention (C1–C14).
/// </summary>
/// <remarks>
/// <para>
/// Every racing caller owns its own host, scope and context, under its own <c>application_name</c>. Contention is
/// proved by reading <c>pg_stat_activity</c> and <c>pg_blocking_pids</c>, never by a delay; that monitoring is
/// read-only, and no session is ever signalled by PID.
/// </para>
/// <para>
/// Side effects "between statements" are run by per-session interceptors that never alter a real command's result.
/// Each side effect owns its writer connection and is admitted through a <see cref="SideEffectGate"/>. Cleanup cancels
/// through the real tokens, drains every tracked operation within finite phase deadlines, and never treats a timeout
/// as proof that anything finished: an unresolved operation keeps its resources and fails the test.
/// </para>
/// </remarks>
public sealed class NotificationConcurrencyTests(PostgreSqlFixture fixture) : DatabaseTest(fixture)
{
    private const string SuspendSql = "UPDATE identity.users SET status = 'Suspended', updated_at = @at WHERE id = @id";
    private const string ActivateSql = "UPDATE identity.users SET status = 'Active', updated_at = @at WHERE id = @id";
    private const string DeleteNotificationSql = "DELETE FROM notifications.notifications WHERE id = @id";

    private const string InsertPasswordChangedSql =
        """
        INSERT INTO notifications.notifications
            (id, recipient_user_id, type, occurrence_id, visibility_kind, subject_type, subject_id, parameters, created_at, read_at)
        VALUES (@id, @recipient, 'security.password_changed', @occurrence, 1, NULL, NULL, '{}'::jsonb, @createdAt, NULL)
        """;

    private static readonly Func<Guid, NotificationVisibilityScope> CreateScope = BindScopeFactory();

    private static readonly TimeSpan BlockerDiscoveryDeadline = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan ScenarioDeadline = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan OperationWait = TimeSpan.FromSeconds(30);

    private static readonly DateTimeOffset CreatedAt = TestClock.DefaultNow.AddMinutes(1);
    private static readonly DateTimeOffset StatusChangedAt = TestClock.DefaultNow.AddHours(1);

    // C1
    [Fact]
    public async Task C1_ConcurrentMarkReadCallsOnOneUnreadRowMarkItExactlyOnce()
    {
        var recipient = await SeedUserAsync();
        var id = await SeedNotificationAsync(recipient);
        var scope = CreateScope(recipient);

        await RunAsync(async run =>
        {
            var holder = await run.BeginHolderAsync();
            await holder.LockAsync(id, run.Token);

            var callers = Enumerable.Range(1, 4).Select(index => run.CreateCaller($"c{index}")).ToArray();
            var calls = callers
                .Select((caller, index) => caller.Start(
                    repository => repository.MarkReadAsync(scope, id, At(index + 1), run.Token)))
                .ToArray();

            await run.WaitUntilBlockedAsync(holder, callers);
            await holder.RollbackAsync(run.Token);

            var outcomes = await run.AwaitAsync(Task.WhenAll(calls));

            var winner = Assert.Single(
                Enumerable.Range(0, outcomes.Length),
                index => outcomes[index] == NotificationReadOutcome.MarkedRead);
            Assert.Equal(3, outcomes.Count(outcome => outcome == NotificationReadOutcome.AlreadyRead));
            Assert.Equal(At(winner + 1), await ReadAtAsync(id));
        });
    }

    // C2
    [Fact]
    public async Task C2_OverlappingBulkCallsShareOneContestedRowAndMarkEveryRowOnce()
    {
        var recipient = await SeedUserAsync();
        var contested = await SeedNotificationAsync(recipient);
        var firstOnly = new[] { await SeedNotificationAsync(recipient), await SeedNotificationAsync(recipient) };
        var secondOnly = new[] { await SeedNotificationAsync(recipient), await SeedNotificationAsync(recipient) };
        var scope = CreateScope(recipient);

        await RunAsync(async run =>
        {
            // Only the contested row is locked; every other row belongs to exactly one caller, so no multi-row
            // deadlock is possible.
            var holder = await run.BeginHolderAsync();
            await holder.LockAsync(contested, run.Token);

            var first = run.CreateCaller("c1");
            var second = run.CreateCaller("c2");
            var firstCall = first.Start(repository => repository.MarkDisplayedReadAsync(
                scope, [contested, firstOnly[0], firstOnly[1]], At(1), run.Token));
            var secondCall = second.Start(repository => repository.MarkDisplayedReadAsync(
                scope, [contested, secondOnly[0], secondOnly[1]], At(2), run.Token));

            await run.WaitUntilBlockedAsync(holder, first, second);
            await holder.RollbackAsync(run.Token);

            var counts = await run.AwaitAsync(Task.WhenAll(firstCall, secondCall));

            Assert.Equal(5, counts.Sum());
            var winner = Assert.Single(Enumerable.Range(0, 2), index => counts[index] == 3);
            Assert.Equal(2, counts[1 - winner]);
            Assert.Equal(At(winner + 1), await ReadAtAsync(contested));
            Assert.Equal(At(1), await ReadAtAsync(firstOnly[0]));
            Assert.Equal(At(1), await ReadAtAsync(firstOnly[1]));
            Assert.Equal(At(2), await ReadAtAsync(secondOnly[0]));
            Assert.Equal(At(2), await ReadAtAsync(secondOnly[1]));
        });
    }

    // C3
    [Fact]
    public async Task C3_MarkReadAndBulkMarkingRaceForOneRowAndExactlyOneReportsTheChange()
    {
        var recipient = await SeedUserAsync();
        var contested = await SeedNotificationAsync(recipient);
        var other = await SeedNotificationAsync(recipient);
        var scope = CreateScope(recipient);

        await RunAsync(async run =>
        {
            var holder = await run.BeginHolderAsync();
            await holder.LockAsync(contested, run.Token);

            var single = run.CreateCaller("c1");
            var bulk = run.CreateCaller("c2");
            var singleCall = single.Start(repository => repository.MarkReadAsync(scope, contested, At(1), run.Token));
            var bulkCall = bulk.Start(repository => repository.MarkDisplayedReadAsync(scope, [contested, other], At(2), run.Token));

            await run.WaitUntilBlockedAsync(holder, single, bulk);
            await holder.RollbackAsync(run.Token);

            var outcome = await run.AwaitAsync(singleCall);
            var count = await run.AwaitAsync(bulkCall);

            if (outcome == NotificationReadOutcome.MarkedRead)
            {
                Assert.Equal(1, count);
                Assert.Equal(At(1), await ReadAtAsync(contested));
            }
            else
            {
                Assert.Equal(NotificationReadOutcome.AlreadyRead, outcome);
                Assert.Equal(2, count);
                Assert.Equal(At(2), await ReadAtAsync(contested));
            }

            Assert.Equal(At(2), await ReadAtAsync(other));
        });
    }

    // C4
    [Fact]
    public async Task C4_InactiveAtTheUpdateAndActiveAtTheProbeRetriesAndMarksRead()
    {
        var recipient = await SeedUserAsync();
        var id = await SeedNotificationAsync(recipient);
        await SetUserStatusAsync(recipient, "Suspended");
        var scope = CreateScope(recipient);

        await RunAsync(async run =>
        {
            var caller = run.CreateCaller("c1");
            caller.Interceptor.AfterUpdate = async hook =>
            {
                if (hook.Attempt == 1 && hook.RowsAffected == 0)
                {
                    await hook.WriteAsync(ActivateSql, ("at", StatusChangedAt), ("id", recipient));
                }
            };
            caller.Interceptor.Arm();

            var outcome = await run.AwaitAsync(caller.Start(
                repository => repository.MarkReadAsync(scope, id, At(1), run.Token)));
            await run.DisarmAsync();

            // U(0), P(unread), U(1).
            Assert.Equal(NotificationReadOutcome.MarkedRead, outcome);
            Assert.Equal(["UPDATE", "SELECT", "UPDATE"], caller.Interceptor.AttemptedKinds());
            Assert.Equal([0, 1], caller.Interceptor.UpdateRowsAffected());
            Assert.Equal(At(1), await ReadAtAsync(id));
        });
    }

    // C5
    [Fact]
    public async Task C5_AlreadyReadThenSuspendedBetweenTheUpdateAndTheProbeIsNotFound()
    {
        var recipient = await SeedUserAsync();
        var id = await SeedNotificationAsync(recipient, readAt: At(1));
        var scope = CreateScope(recipient);

        await RunAsync(async run =>
        {
            var caller = run.CreateCaller("c1");
            caller.Interceptor.AfterUpdate = async hook =>
            {
                if (hook.Attempt == 1 && hook.RowsAffected == 0)
                {
                    await hook.WriteAsync(SuspendSql, ("at", StatusChangedAt), ("id", recipient));
                }
            };
            caller.Interceptor.Arm();

            var outcome = await run.AwaitAsync(caller.Start(
                repository => repository.MarkReadAsync(scope, id, At(2), run.Token)));
            await run.DisarmAsync();

            // U(0), P(empty).
            Assert.Equal(NotificationReadOutcome.NotFound, outcome);
            Assert.Equal(["UPDATE", "SELECT"], caller.Interceptor.AttemptedKinds());
            Assert.Equal([0], caller.Interceptor.UpdateRowsAffected());
            Assert.Equal(At(1), await ReadAtAsync(id));
        });
    }

    // C6
    [Fact]
    public async Task C6_ASuspensionCommittedDuringTheUpdatesLockWaitIsNotSeenByThatStatement()
    {
        var recipient = await SeedUserAsync();
        var id = await SeedNotificationAsync(recipient);
        var scope = CreateScope(recipient);

        await RunAsync(async run =>
        {
            var holder = await run.BeginHolderAsync();
            await holder.LockAsync(id, run.Token);

            var caller = run.CreateCaller("c1");
            var call = caller.Start(repository => repository.MarkReadAsync(scope, id, At(1), run.Token));

            // The server confirms the UPDATE is waiting on the holder before the suspension is committed.
            await run.WaitUntilBlockedAsync(holder, caller);
            await run.WriteAsync(SuspendSql, ("at", StatusChangedAt), ("id", recipient));
            await holder.RollbackAsync(run.Token);

            var outcome = await run.AwaitAsync(call);

            if (outcome != NotificationReadOutcome.MarkedRead)
            {
                Assert.Fail(await run.DescribeAsync(
                    $"Frozen C6 expectation violated: PostgreSQL produced {outcome}, not MarkedRead. "
                    + "A contract amendment is required; the assertion must not be loosened."));
            }

            Assert.Equal(At(1), await ReadAtAsync(id));

            // The next statement sees the committed suspension.
            var next = await run.AwaitAsync(caller.Start(
                repository => repository.MarkReadAsync(scope, id, At(2), run.Token)));
            Assert.Equal(NotificationReadOutcome.NotFound, next);
            Assert.Equal(At(1), await ReadAtAsync(id));
        });
    }

    // C7
    [Fact]
    public async Task C7_AHolderThatWritesTheFirstReadAfterTheConfirmedWaitKeepsItsTime()
    {
        var recipient = await SeedUserAsync();
        var id = await SeedNotificationAsync(recipient);
        var scope = CreateScope(recipient);

        await RunAsync(async run =>
        {
            var holder = await run.BeginHolderAsync();
            await holder.LockAsync(id, run.Token);

            var caller = run.CreateCaller("c1");
            var call = caller.Start(repository => repository.MarkReadAsync(scope, id, At(2), run.Token));

            // Only after the server-confirmed wait does the holder write T0 and commit, so a sequential run of the
            // caller cannot satisfy this test.
            await run.WaitUntilBlockedAsync(holder, caller);
            Assert.False(call.IsCompleted, "The caller finished before the holder wrote the first read.");
            await holder.ExecuteAsync(
                "UPDATE notifications.notifications SET read_at = @readAt WHERE id = @id",
                run.Token,
                ("readAt", At(1)),
                ("id", id));
            await holder.CommitAsync(run.Token);

            var outcome = await run.AwaitAsync(call);

            Assert.Equal(NotificationReadOutcome.AlreadyRead, outcome);
            Assert.Equal(["UPDATE", "SELECT"], caller.Interceptor.AttemptedKinds());
            Assert.Equal([0], caller.Interceptor.UpdateRowsAffected());
            Assert.Equal(At(1), await ReadAtAsync(id));
        });
    }

    // C8
    [Fact]
    public async Task C8_ARetentionDeletionBetweenTheUpdateAndTheProbeIsNotFound()
    {
        var recipient = await SeedUserAsync();
        var id = await SeedNotificationAsync(recipient);
        await SetUserStatusAsync(recipient, "Suspended");
        var scope = CreateScope(recipient);

        await RunAsync(async run =>
        {
            var caller = run.CreateCaller("c1");
            caller.Interceptor.AfterUpdate = async hook =>
            {
                if (hook.Attempt == 1 && hook.RowsAffected == 0)
                {
                    await hook.WriteAsync(DeleteNotificationSql, ("id", id));
                    await hook.WriteAsync(ActivateSql, ("at", StatusChangedAt), ("id", recipient));
                }
            };
            caller.Interceptor.Arm();

            var outcome = await run.AwaitAsync(caller.Start(
                repository => repository.MarkReadAsync(scope, id, At(1), run.Token)));
            await run.DisarmAsync();

            Assert.Equal(NotificationReadOutcome.NotFound, outcome);
            Assert.Equal(["UPDATE", "SELECT"], caller.Interceptor.AttemptedKinds());
            Assert.Equal([0], caller.Interceptor.UpdateRowsAffected());
            Assert.False(await NotificationExistsAsync(id));
        });
    }

    // C9
    [Fact]
    public async Task C9_ANotificationInsertedBetweenTheUpdateAndTheProbeIsRetriedAndMarkedRead()
    {
        var recipient = await SeedUserAsync();
        var id = Guid.NewGuid();
        var scope = CreateScope(recipient);

        await RunAsync(async run =>
        {
            var caller = run.CreateCaller("c1");
            caller.Interceptor.AfterUpdate = async hook =>
            {
                if (hook.Attempt == 1 && hook.RowsAffected == 0)
                {
                    await hook.WriteAsync(
                        InsertPasswordChangedSql,
                        ("id", id),
                        ("recipient", recipient),
                        ("occurrence", Guid.NewGuid()),
                        ("createdAt", CreatedAt));
                }
            };
            caller.Interceptor.Arm();

            var outcome = await run.AwaitAsync(caller.Start(
                repository => repository.MarkReadAsync(scope, id, At(1), run.Token)));
            await run.DisarmAsync();

            Assert.Equal(NotificationReadOutcome.MarkedRead, outcome);
            Assert.Equal(["UPDATE", "SELECT", "UPDATE"], caller.Interceptor.AttemptedKinds());
            Assert.Equal([0, 1], caller.Interceptor.UpdateRowsAffected());
            Assert.Equal(At(1), await ReadAtAsync(id));
        });
    }

    // C10
    [Fact]
    public async Task C10_ThreeVisibleUnreadFallbackObservationsExhaustTheRetriesExplicitly()
    {
        var recipient = await SeedUserAsync();
        var id = await SeedNotificationAsync(recipient);
        var scope = CreateScope(recipient);

        await RunAsync(async run =>
        {
            var caller = run.CreateCaller("c1");
            caller.Interceptor.BeforeUpdate = hook => hook.WriteAsync(SuspendSql, ("at", StatusChangedAt), ("id", recipient));
            caller.Interceptor.AfterUpdate = hook => hook.WriteAsync(ActivateSql, ("at", StatusChangedAt), ("id", recipient));
            caller.Interceptor.Arm();

            var call = caller.Start(repository => repository.MarkReadAsync(scope, id, At(1), run.Token));
            var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => run.AwaitAsync(call));
            await run.DisarmAsync();

            Assert.Contains("after 3 attempts", exception.Message, StringComparison.Ordinal);
            Assert.Equal(
                ["UPDATE", "SELECT", "UPDATE", "SELECT", "UPDATE", "SELECT"],
                caller.Interceptor.AttemptedKinds());
            Assert.Equal([0, 0, 0], caller.Interceptor.UpdateRowsAffected());
            Assert.Null(await ReadAtAsync(id));
        });
    }

    // C11
    [Fact]
    public async Task C11_AProbeFailureAfterARealZeroRowUpdatePropagatesTheSameExceptionWithoutRetry()
    {
        var recipient = await SeedUserAsync();
        var id = await SeedNotificationAsync(recipient, readAt: At(1));
        var scope = CreateScope(recipient);
        var sentinel = new ProbeSentinelException();

        await RunAsync(async run =>
        {
            var caller = run.CreateCaller("c1");
            caller.Interceptor.BeforeSelect = _ => throw sentinel;
            caller.Interceptor.Arm();

            var call = caller.Start(repository => repository.MarkReadAsync(scope, id, At(2), run.Token));
            var thrown = await Assert.ThrowsAnyAsync<Exception>(() => run.AwaitAsync(call));
            await run.DisarmAsync();

            Assert.Same(sentinel, thrown);

            var records = caller.Interceptor.Records();
            var update = Assert.Single(records, record => record.Kind == CommandKind.Update);
            Assert.Equal(CommandStatus.Executed, update.Status);
            Assert.Equal(0, update.RowsAffected);

            // The probe was attempted; it never created a reader. A missing callback alone is not read as failure:
            // the caught sentinel is what establishes it.
            var probe = Assert.Single(records, record => record.Kind == CommandKind.Select);
            Assert.NotEqual(CommandStatus.ReaderCreated, probe.Status);
            Assert.NotEqual(CommandStatus.Executed, probe.Status);

            Assert.Equal(At(1), await ReadAtAsync(id));
        });
    }

    // C12
    [Fact]
    public async Task C12_CancellationDuringAVerifiedLockWaitEndsTheCallAndChangesNothing()
    {
        var recipient = await SeedUserAsync();
        var id = await SeedNotificationAsync(recipient);
        var scope = CreateScope(recipient);

        await RunAsync(async run =>
        {
            var holder = await run.BeginHolderAsync();
            await holder.LockAsync(id, run.Token);

            var cancellation = run.CreateCancellation();
            var caller = run.CreateCaller("c1");
            var call = caller.Start(repository => repository.MarkReadAsync(scope, id, At(1), cancellation.Token));

            await run.WaitUntilBlockedAsync(holder, caller);
            await cancellation.CancelAsync();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run.AwaitAsync(call));
            await run.WaitUntilNoLongerWaitingAsync(caller);
            await holder.RollbackAsync(run.Token);

            Assert.Null(await ReadAtAsync(id));
        });
    }

    // C13
    [Fact]
    public async Task C13_ASessionLocalLockTimeoutPropagatesWithoutAProbeOrRetry()
    {
        var recipient = await SeedUserAsync();
        var id = await SeedNotificationAsync(recipient);
        var scope = CreateScope(recipient);

        await RunAsync(async run =>
        {
            // The holder's lock is established first. A 500 ms wait can end before any poll could observe it, so
            // this scenario deliberately does not use blocker discovery.
            var holder = await run.BeginHolderAsync();
            await holder.LockAsync(id, run.Token);

            var caller = run.CreateCaller("c1", lockTimeoutMilliseconds: 500);
            var call = caller.Start(repository => repository.MarkReadAsync(scope, id, At(1), run.Token));

            var exception = await Assert.ThrowsAsync<PostgresException>(() => run.AwaitAsync(call));
            Assert.Equal(PostgresErrorCodes.LockNotAvailable, exception.SqlState);
            Assert.Equal(["UPDATE"], caller.Interceptor.AttemptedKinds());

            await holder.RollbackAsync(run.Token);
            Assert.Null(await ReadAtAsync(id));
        });
    }

    // C14
    [Fact]
    public async Task C14_SuspendedThenActiveHidesEverythingAndThenRestoresAccessWithoutChangingHistory()
    {
        var recipient = await SeedUserAsync();
        var unread = await SeedNotificationAsync(recipient);
        var alreadyRead = await SeedNotificationAsync(recipient, readAt: At(1));
        await SetUserStatusAsync(recipient, "Suspended");
        var scope = CreateScope(recipient);

        await RunAsync(async run =>
        {
            var caller = run.CreateCaller("c1");

            Assert.Equal(
                NotificationReadOutcome.NotFound,
                await run.AwaitAsync(caller.Start(repository => repository.MarkReadAsync(scope, unread, At(2), run.Token))));
            Assert.Equal(
                0,
                await run.AwaitAsync(caller.Start(
                    repository => repository.MarkDisplayedReadAsync(scope, [unread, alreadyRead], At(2), run.Token))));
            var hidden = await run.AwaitAsync(caller.Start(repository => repository.ListAsync(scope, false, 0, 10, run.Token)));
            Assert.Empty(hidden.Items);
            Assert.Equal(0L, hidden.TotalCount);
            Assert.Equal(0, await run.AwaitAsync(caller.Start(repository => repository.CountUnreadAsync(scope, run.Token))));
            Assert.Null(await ReadAtAsync(unread));

            await run.WriteAsync(ActivateSql, ("at", StatusChangedAt), ("id", recipient));

            Assert.Equal(
                NotificationReadOutcome.MarkedRead,
                await run.AwaitAsync(caller.Start(repository => repository.MarkReadAsync(scope, unread, At(2), run.Token))));
            Assert.Equal(At(2), await ReadAtAsync(unread));
            Assert.Equal(At(1), await ReadAtAsync(alreadyRead));
            Assert.Equal(CreatedAt, await CreatedAtAsync(unread));
        });
    }

    private static DateTimeOffset At(int seconds)
    {
        // Whole seconds after creation: already at storage precision, distinct, and never clamped to CreatedAt.
        return CreatedAt.AddSeconds(seconds);
    }

    // Runs a scenario under its own orchestrator. The scenario's own failure stays the primary error; cleanup
    // diagnostics are attached to it rather than replacing it.
    private async Task RunAsync(Func<Orchestrator, Task> scenario)
    {
        var run = new Orchestrator(Host.ConnectionString);
        Task body;

        try
        {
            body = scenario(run);
        }
        catch (Exception exception)
        {
            body = Task.FromException(exception);
        }

        Exception? failure = null;

        if (await Task.WhenAny(body, Task.Delay(ScenarioDeadline)) == body)
        {
            try
            {
                await body;
            }
            catch (Exception exception)
            {
                failure = exception;
            }
        }
        else
        {
            failure = new TimeoutException($"The scenario did not finish within {ScenarioDeadline}.");
        }

        var cleanupFailure = await run.CleanupAsync(body);

        if (failure is null && cleanupFailure is null)
        {
            return;
        }

        if (cleanupFailure is null)
        {
            ExceptionDispatchInfo.Capture(failure!).Throw();
        }

        if (failure is null)
        {
            throw cleanupFailure;
        }

        throw new AggregateException("The scenario failed, and its cleanup did not complete safely.", failure, cleanupFailure);
    }

    private async Task<Guid> SeedUserAsync()
    {
        var user = AccessGraph.ActiveUser($"race-{Guid.NewGuid():n}@example.test");

        await using var context = Host.CreateVerificationContext();
        context.Users.Add(user);
        await context.SaveChangesAsync();

        return user.Id;
    }

    private async Task<Guid> SeedNotificationAsync(Guid recipient, DateTimeOffset? readAt = null)
    {
        var notification = Notification.CreateMemberAdded(Guid.NewGuid(), recipient, Guid.NewGuid(), Guid.NewGuid(), CreatedAt);

        await using (var context = Host.CreateVerificationContext())
        {
            context.Set<Notification>().Add(notification);
            await context.SaveChangesAsync();
        }

        if (readAt is { } value)
        {
            await ExecuteAsync(
                "UPDATE notifications.notifications SET read_at = @readAt WHERE id = @id",
                ("readAt", value),
                ("id", notification.Id));
        }

        return notification.Id;
    }

    private Task SetUserStatusAsync(Guid userId, string status)
    {
        return ExecuteAsync(
            "UPDATE identity.users SET status = @status, updated_at = @at WHERE id = @id",
            ("status", status),
            ("at", StatusChangedAt),
            ("id", userId));
    }

    private async Task<DateTimeOffset?> ReadAtAsync(Guid id)
    {
        await using var connection = new NpgsqlConnection(Host.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("SELECT read_at FROM notifications.notifications WHERE id = @id", connection);
        command.Parameters.AddWithValue("id", id);
        await using var reader = await command.ExecuteReaderAsync();

        Assert.True(await reader.ReadAsync(), $"Notification {id} does not exist.");

        return reader.IsDBNull(0) ? null : reader.GetFieldValue<DateTimeOffset>(0);
    }

    private async Task<DateTimeOffset> CreatedAtAsync(Guid id)
    {
        await using var connection = new NpgsqlConnection(Host.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("SELECT created_at FROM notifications.notifications WHERE id = @id", connection);
        command.Parameters.AddWithValue("id", id);
        await using var reader = await command.ExecuteReaderAsync();

        Assert.True(await reader.ReadAsync(), $"Notification {id} does not exist.");

        return reader.GetFieldValue<DateTimeOffset>(0);
    }

    private async Task<bool> NotificationExistsAsync(Guid id)
    {
        await using var connection = new NpgsqlConnection(Host.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("SELECT count(*) FROM notifications.notifications WHERE id = @id", connection);
        command.Parameters.AddWithValue("id", id);

        return (long)(await command.ExecuteScalarAsync())! == 1L;
    }

    private async Task ExecuteAsync(string sql, params (string Name, object Value)[] parameters)
    {
        await using var connection = new NpgsqlConnection(Host.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);

        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        await command.ExecuteNonQueryAsync();
    }

    private static async Task ExecuteOnAsync(
        string connectionString,
        string sql,
        CancellationToken cancellationToken,
        (string Name, object Value)[] parameters)
    {
        // The connection belongs to this call alone and is disposed before it returns or throws.
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);

        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        await command.ExecuteNonQueryAsync(cancellationToken);
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

    private sealed class ProbeSentinelException : Exception
    {
        public ProbeSentinelException()
            : base("Test-owned probe failure.")
        {
        }
    }

    private enum OperationState
    {
        Running,
        Completed,
        Faulted,
        Canceled,
        Unresolved
    }

    // A teardown decision, read from the step's actual task: only Succeeded or Failed is terminated.
    private enum StepOutcome
    {
        Succeeded,
        Failed,
        Unresolved
    }

    /// <summary>A task the cleanup must account for, with the state it was last observed in.</summary>
    private sealed class TrackedOperation
    {
        public TrackedOperation(string name, Task task)
        {
            Name = name;
            Task = task;
        }

        public string Name { get; }

        public Task Task { get; }

        public OperationState State { get; private set; } = OperationState.Running;

        public Exception? Error { get; private set; }

        // A completed task's exception is read here, so it is always observed. A task still running is unresolved:
        // its eventual outcome is recorded in the diagnostics, which are not claimed to reach a report already made.
        public void Settle(string phase, ConcurrentQueue<string> diagnostics)
        {
            if (Task.IsCompleted)
            {
                Error = Task.Exception?.GetBaseException();
                State = Task.IsCanceled ? OperationState.Canceled
                    : Task.IsFaulted ? OperationState.Faulted
                    : OperationState.Completed;

                return;
            }

            if (State == OperationState.Unresolved)
            {
                return;
            }

            State = OperationState.Unresolved;
            var name = Name;
            Task.ContinueWith(
                finished => diagnostics.Enqueue(
                    $"Late outcome of {name} (unresolved at {phase}): {finished.Status} {finished.Exception?.GetBaseException().Message}"),
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }

        public override string ToString()
        {
            return $"{Name}: {State}{(Error is null ? string.Empty : $" ({Error.GetType().Name}: {Error.Message})")}";
        }
    }

    /// <summary>
    /// Admits interceptor side effects while Running and drains them once stopping begins. The drain signal is created
    /// only when stopping begins, so an earlier moment with nothing in flight can never satisfy it.
    /// </summary>
    private sealed class SideEffectGate
    {
        private readonly Lock sync = new();
        private readonly HashSet<Ticket> admitted = [];
        private GateState state = GateState.Running;
        private TaskCompletionSource? drained;
        private long nextTicket;

        private enum GateState
        {
            Running,
            Stopping,
            Stopped
        }

        public bool IsStopped
        {
            get
            {
                lock (sync)
                {
                    return state == GateState.Stopped;
                }
            }
        }

        public Ticket? TryEnter(string phase)
        {
            lock (sync)
            {
                // Admission closes atomically with the state change.
                if (state != GateState.Running)
                {
                    return null;
                }

                var ticket = new Ticket(++nextTicket, phase);
                admitted.Add(ticket);

                return ticket;
            }
        }

        // Exactly once per ticket: a second exit for the same ticket finds nothing to remove and changes nothing.
        public void Exit(Ticket ticket)
        {
            lock (sync)
            {
                if (!admitted.Remove(ticket))
                {
                    return;
                }

                if (state == GateState.Stopping && admitted.Count == 0)
                {
                    state = GateState.Stopped;
                    drained!.TrySetResult();
                }
            }
        }

        // Repeated calls return the same drain task.
        public Task BeginStop()
        {
            lock (sync)
            {
                if (state == GateState.Running)
                {
                    state = GateState.Stopping;
                    drained = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

                    if (admitted.Count == 0)
                    {
                        state = GateState.Stopped;
                        drained.TrySetResult();
                    }
                }

                return drained!.Task;
            }
        }

        public IReadOnlyList<string> Outstanding()
        {
            lock (sync)
            {
                return [.. admitted.Select(ticket => ticket.ToString())];
            }
        }

        public sealed class Ticket(long id, string phase)
        {
            public override string ToString()
            {
                return $"#{id} {phase}";
            }
        }
    }

    private enum CommandKind
    {
        Update,
        Select,
        Other
    }

    private enum CommandStatus
    {
        Attempted,
        Executed,
        ReaderCreated,
        Failed
    }

    /// <summary>One command attempt: Executing records it; the completion callbacks only ever move it forward.</summary>
    private sealed class CommandRecord
    {
        public CommandRecord(CommandKind kind, string text, int processId)
        {
            Kind = kind;
            Text = text;
            ProcessId = processId;
        }

        public CommandKind Kind { get; }

        public string Text { get; }

        public int ProcessId { get; }

        public int Attempt { get; set; }

        public CommandStatus Status { get; set; } = CommandStatus.Attempted;

        public int? RowsAffected { get; set; }

        public Exception? Error { get; set; }

        public override string ToString()
        {
            return $"{Kind}#{Attempt} pid={ProcessId} {Status} rows={RowsAffected?.ToString() ?? "-"} {Error?.GetType().Name}";
        }
    }

    /// <summary>The arguments and owned resources one interceptor side effect receives.</summary>
    private sealed class HookContext
    {
        private readonly string writerConnectionString;
        private readonly CancellationToken cancellationToken;

        public HookContext(int attempt, int? rowsAffected, string writerConnectionString, CancellationToken cancellationToken)
        {
            Attempt = attempt;
            RowsAffected = rowsAffected;
            this.writerConnectionString = writerConnectionString;
            this.cancellationToken = cancellationToken;
        }

        public int Attempt { get; }

        public int? RowsAffected { get; }

        // Each write opens, uses and disposes its own unpooled writer connection inside this invocation.
        public Task WriteAsync(string sql, params (string Name, object Value)[] parameters)
        {
            return ExecuteOnAsync(writerConnectionString, sql, cancellationToken, parameters);
        }
    }

    /// <summary>
    /// One caller session's observer: records every attempt with its backend PID, distinguishes execution, reader
    /// creation and failure, and runs this session's armed side effects through its own gate. It never consumes a
    /// reader and never changes a command's real result.
    /// </summary>
    private sealed class SessionInterceptor : DbCommandInterceptor
    {
        private readonly Lock sync = new();
        private readonly List<CommandRecord> records = [];
        private readonly Dictionary<Guid, CommandRecord> byCommandId = [];
        private readonly TaskCompletionSource updateStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly string name;
        private readonly string writerConnectionString;
        private readonly CancellationToken cancellationToken;
        private int armed;
        private int updates;
        private int selects;

        public SessionInterceptor(string name, string writerConnectionString, CancellationToken cancellationToken)
        {
            this.name = name;
            this.writerConnectionString = writerConnectionString;
            this.cancellationToken = cancellationToken;
        }

        public SideEffectGate Gate { get; } = new();

        public Func<HookContext, Task>? BeforeUpdate { get; set; }

        public Func<HookContext, Task>? AfterUpdate { get; set; }

        public Func<HookContext, Task>? BeforeSelect { get; set; }

        public Task UpdateStarted => updateStarted.Task;

        public int? LastUpdateProcessId
        {
            get
            {
                lock (sync)
                {
                    return records.LastOrDefault(record => record.Kind == CommandKind.Update)?.ProcessId;
                }
            }
        }

        public void Arm()
        {
            Volatile.Write(ref armed, 1);
        }

        public void ReleaseBarriers()
        {
            updateStarted.TrySetCanceled();
        }

        public IReadOnlyList<CommandRecord> Records()
        {
            lock (sync)
            {
                return [.. records];
            }
        }

        public IReadOnlyList<string> AttemptedKinds()
        {
            return [.. Records().Where(record => record.Kind != CommandKind.Other).Select(record => record.Kind == CommandKind.Update ? "UPDATE" : "SELECT")];
        }

        public IReadOnlyList<int> UpdateRowsAffected()
        {
            return [.. Records()
                .Where(record => record.Kind == CommandKind.Update && record.Status == CommandStatus.Executed)
                .Select(record => record.RowsAffected ?? -1)];
        }

        public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            var record = Attempt(command, eventData);

            if (record.Kind == CommandKind.Update)
            {
                try
                {
                    await RunHookAsync(BeforeUpdate, record.Attempt, null, "before-update");
                }
                finally
                {
                    updateStarted.TrySetResult();
                }
            }

            return result;
        }

        public override async ValueTask<int> NonQueryExecutedAsync(
            DbCommand command,
            CommandExecutedEventData eventData,
            int result,
            CancellationToken cancellationToken = default)
        {
            var record = Complete(eventData.CommandId, CommandStatus.Executed, result, null);

            if (record is { Kind: CommandKind.Update })
            {
                await RunHookAsync(AfterUpdate, record.Attempt, result, "after-update");
            }

            return result;
        }

        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            var record = Attempt(command, eventData);

            if (record.Kind == CommandKind.Select)
            {
                await RunHookAsync(BeforeSelect, record.Attempt, null, "before-select");
            }

            return result;
        }

        public override ValueTask<DbDataReader> ReaderExecutedAsync(
            DbCommand command,
            CommandExecutedEventData eventData,
            DbDataReader result,
            CancellationToken cancellationToken = default)
        {
            // A reader exists; nothing here says its rows were consumed.
            Complete(eventData.CommandId, CommandStatus.ReaderCreated, null, null);

            return ValueTask.FromResult(result);
        }

        public override Task CommandFailedAsync(
            DbCommand command,
            CommandErrorEventData eventData,
            CancellationToken cancellationToken = default)
        {
            Complete(eventData.CommandId, CommandStatus.Failed, null, eventData.Exception);

            return Task.CompletedTask;
        }

        public override void CommandFailed(DbCommand command, CommandErrorEventData eventData)
        {
            Complete(eventData.CommandId, CommandStatus.Failed, null, eventData.Exception);
        }

        private CommandRecord Attempt(DbCommand command, CommandEventData eventData)
        {
            var text = command.CommandText;
            var trimmed = text.TrimStart();
            var touchesNotifications = text.Contains("notifications.notifications", StringComparison.OrdinalIgnoreCase);
            var kind = !touchesNotifications ? CommandKind.Other
                : trimmed.StartsWith("UPDATE", StringComparison.OrdinalIgnoreCase) ? CommandKind.Update
                : trimmed.StartsWith("SELECT", StringComparison.OrdinalIgnoreCase) ? CommandKind.Select
                : CommandKind.Other;
            var processId = command.Connection is NpgsqlConnection connection ? connection.ProcessID : 0;
            var record = new CommandRecord(kind, text, processId);

            lock (sync)
            {
                record.Attempt = kind switch
                {
                    CommandKind.Update => ++updates,
                    CommandKind.Select => ++selects,
                    _ => 0
                };
                records.Add(record);
                byCommandId[eventData.CommandId] = record;
            }

            return record;
        }

        private CommandRecord? Complete(Guid commandId, CommandStatus status, int? rowsAffected, Exception? error)
        {
            lock (sync)
            {
                if (!byCommandId.TryGetValue(commandId, out var record))
                {
                    return null;
                }

                record.Status = status;
                record.RowsAffected = rowsAffected ?? record.RowsAffected;
                record.Error = error ?? record.Error;

                return record;
            }
        }

        private async Task RunHookAsync(Func<HookContext, Task>? hook, int attempt, int? rowsAffected, string phase)
        {
            if (hook is null || Volatile.Read(ref armed) == 0)
            {
                return;
            }

            var ticket = Gate.TryEnter($"{name}:{phase}#{attempt}");

            if (ticket is null)
            {
                return;
            }

            try
            {
                await hook(new HookContext(attempt, rowsAffected, writerConnectionString, cancellationToken));
            }
            finally
            {
                // Runs after the hook's own writer connection was disposed, or after it faulted.
                Gate.Exit(ticket);
            }
        }
    }

    private enum OperationKind
    {
        CallerOperation,
        HolderCreation,
        MainWrite,
        Poll
    }

    /// <summary>
    /// One scenario operation admitted by the orchestrator. It is registered, and its task observed, before its
    /// delegate is queued; being queued is never taken as having started.
    /// </summary>
    private sealed class AdmittedOperation
    {
        private int started;

        public AdmittedOperation(long id, string name, OperationKind kind, object? owner, Task task)
        {
            Id = id;
            Name = name;
            Kind = kind;
            Owner = owner;
            Task = task;
            Tracked = new TrackedOperation(name, task);
        }

        public long Id { get; }

        public string Name { get; }

        public OperationKind Kind { get; }

        public object? Owner { get; }

        public Task Task { get; }

        public TrackedOperation Tracked { get; }

        public bool Started => Volatile.Read(ref started) == 1;

        public Exception? NotStartedReason { get; private set; }

        public void MarkStarted()
        {
            Volatile.Write(ref started, 1);
        }

        public void MarkNotStarted(Exception reason)
        {
            NotStartedReason = reason;
        }

        public override string ToString()
        {
            var execution = Started ? "started" : NotStartedReason is null ? "not started" : $"never started ({NotStartedReason.GetType().Name})";
            return $"#{Id} {Name} [{Kind}, {execution}, {Task.Status}]";
        }
    }

    private sealed class ScenarioStoppedException(string operation)
        : InvalidOperationException($"The scenario is stopping; '{operation}' was not admitted.");

    private sealed class DiscoveryStageEndedException(string stage)
        : Exception($"Discovery stage '{stage}' ended without an accepted observation.");

    private sealed class PollerClosedException()
        : InvalidOperationException("The poller is terminal; no further observation may use it.");

    private enum HolderState
    {
        Open,
        Stopping,
        Terminal
    }

    private sealed class HolderClosedException(int holderId, HolderState state)
        : InvalidOperationException($"Holder #{holderId} is {state}; no further scenario operation may use it.");

    /// <summary>One racing caller: its own host, scope, context and repository, used by one operation at a time.</summary>
    private sealed class Caller
    {
        private readonly Lock sync = new();
        private readonly List<Task> started = [];
        private readonly Orchestrator owner;
        private readonly PersistenceTestHost host;
        private readonly IServiceScope scope;
        private readonly INotificationRepository repository;

        public Caller(Orchestrator owner, string name, string applicationName, PersistenceTestHost host, SessionInterceptor interceptor)
        {
            this.owner = owner;
            this.host = host;
            Name = name;
            ApplicationName = applicationName;
            Interceptor = interceptor;
            scope = host.CreateScope();
            repository = scope.ServiceProvider.GetRequiredService<INotificationRepository>();
        }

        public string Name { get; }

        public string ApplicationName { get; }

        public SessionInterceptor Interceptor { get; }

        public Task? LatestOperation
        {
            get
            {
                lock (sync)
                {
                    return started.Count == 0 ? null : started[^1];
                }
            }
        }

        // Admission is the orchestrator's: once cleanup has begun, no new operation can start on this context.
        public Task<T> Start<T>(Func<INotificationRepository, Task<T>> operation)
        {
            ArgumentNullException.ThrowIfNull(operation);

            int number;

            lock (sync)
            {
                number = started.Count + 1;
            }

            var task = owner.Admit($"{Name} operation #{number}", OperationKind.CallerOperation, this, () => operation(repository));

            lock (sync)
            {
                started.Add(task);
            }

            return task;
        }

        public async Task DisposeAsync()
        {
            if (scope is IAsyncDisposable asyncDisposable)
            {
                await asyncDisposable.DisposeAsync();
            }
            else
            {
                scope.Dispose();
            }

            await host.DisposeAsync();
        }
    }

    /// <summary>
    /// The row-lock holder. Its unpooled connection and transaction belong to the orchestrator alone. Scenario
    /// operations are serialized by the semaphore and admitted only while Open; cleanup stops admission first, then
    /// takes the semaphore and makes the holder Terminal before any teardown, and it never reopens.
    /// </summary>
    private sealed class Holder
    {
        private readonly Lock stateLock = new();
        private readonly SemaphoreSlim exclusive = new(1, 1);
        private readonly NpgsqlConnection connection;
        private NpgsqlTransaction? transaction;
        private HolderState state = HolderState.Open;

        private Holder(int id, NpgsqlConnection connection)
        {
            Id = id;
            this.connection = connection;
        }

        public int Id { get; }

        public int ProcessId { get; private set; }

        public bool Ended { get; private set; }

        public bool HasTransaction => transaction is not null;

        public HolderState State
        {
            get
            {
                lock (stateLock)
                {
                    return state;
                }
            }
        }

        public static async Task<Holder> BeginAsync(int id, string connectionString, CancellationToken cancellationToken)
        {
            var connection = new NpgsqlConnection(connectionString);

            try
            {
                await connection.OpenAsync(cancellationToken);
                var holder = new Holder(id, connection) { ProcessId = connection.ProcessID };
                holder.transaction = await connection.BeginTransactionAsync(cancellationToken);

                return holder;
            }
            catch
            {
                await connection.DisposeAsync();
                throw;
            }
        }

        public Task LockAsync(Guid id, CancellationToken cancellationToken)
        {
            return ExclusiveAsync(
                async () =>
                {
                    await using var command = new NpgsqlCommand(
                        "SELECT id FROM notifications.notifications WHERE id = @id FOR UPDATE",
                        connection,
                        transaction);
                    command.Parameters.AddWithValue("id", id);

                    var locked = 0;

                    await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
                    {
                        while (await reader.ReadAsync(cancellationToken))
                        {
                            locked++;
                        }
                    }

                    if (locked != 1)
                    {
                        throw new InvalidOperationException($"The holder locked {locked} rows instead of one.");
                    }
                },
                cancellationToken);
        }

        public Task ExecuteAsync(string sql, CancellationToken cancellationToken, params (string Name, object Value)[] parameters)
        {
            return ExclusiveAsync(
                async () =>
                {
                    await using var command = new NpgsqlCommand(sql, connection, transaction);

                    foreach (var (name, value) in parameters)
                    {
                        command.Parameters.AddWithValue(name, value);
                    }

                    await command.ExecuteNonQueryAsync(cancellationToken);
                },
                cancellationToken);
        }

        public Task CommitAsync(CancellationToken cancellationToken)
        {
            return ExclusiveAsync(
                async () =>
                {
                    await transaction!.CommitAsync(cancellationToken);
                    Ended = true;
                },
                cancellationToken);
        }

        public Task RollbackAsync(CancellationToken cancellationToken)
        {
            return ExclusiveAsync(
                async () =>
                {
                    await transaction!.RollbackAsync(cancellationToken);
                    Ended = true;
                },
                cancellationToken);
        }

        // Rejects every future scenario operation; an operation already holding the semaphore finishes normally.
        public void BeginStop()
        {
            lock (stateLock)
            {
                if (state == HolderState.Open)
                {
                    state = HolderState.Stopping;
                }
            }
        }

        // Success proves no scenario operation is using the connection; the holder is Terminal from then on.
        public async Task<bool> TryAcquireForTeardownAsync(TimeSpan timeout)
        {
            BeginStop();

            if (!await exclusive.WaitAsync(timeout))
            {
                return false;
            }

            lock (stateLock)
            {
                state = HolderState.Terminal;
            }

            return true;
        }

        public void ReleaseTeardown()
        {
            exclusive.Release();
        }

        // Teardown steps: called only by cleanup while it holds the semaphore. A synchronous throw becomes a faulted task.
        public Task RollbackCoreAsync(CancellationToken cancellationToken)
        {
            var current = transaction;

            return current is null
                ? Task.CompletedTask
                : Start(async () =>
                {
                    await current.RollbackAsync(cancellationToken);
                    Ended = true;
                });
        }

        public Task CloseCoreAsync()
        {
            return Start(() => connection.CloseAsync());
        }

        public Task DisposeTransactionCoreAsync()
        {
            var current = transaction;

            return current is null ? Task.CompletedTask : Start(() => current.DisposeAsync().AsTask());
        }

        public Task DisposeConnectionCoreAsync()
        {
            return Start(() => connection.DisposeAsync().AsTask());
        }

        private static Task Start(Func<Task> operation)
        {
            try
            {
                return operation();
            }
            catch (Exception error)
            {
                return Task.FromException(error);
            }
        }

        private async Task ExclusiveAsync(Func<Task> action, CancellationToken cancellationToken)
        {
            ThrowIfClosed();
            await exclusive.WaitAsync(cancellationToken);

            try
            {
                ThrowIfClosed();

                if (Ended)
                {
                    throw new InvalidOperationException("The holder transaction has already ended.");
                }

                await action();
            }
            finally
            {
                exclusive.Release();
            }
        }

        private void ThrowIfClosed()
        {
            lock (stateLock)
            {
                if (state != HolderState.Open)
                {
                    throw new HolderClosedException(Id, state);
                }
            }
        }
    }

    private enum CreationDisposition
    {
        Pending,
        Published,
        LateRetained
    }

    /// <summary>
    /// One holder creation. Its disposition changes only from Pending, only under the orchestrator lock: Published
    /// (normal cleanup) or LateRetained (never torn down). Finalized is set exactly once by the path that finishes it.
    /// </summary>
    private sealed class PendingCreation(int id)
    {
        public int Id { get; } = id;

        public CreationDisposition Disposition { get; set; } = CreationDisposition.Pending;

        public bool Finalized { get; set; }

        public Holder? Holder { get; set; }

        public Exception? Error { get; set; }
    }

    /// <summary>One poller connection: entered into the inventory before it is opened.</summary>
    private sealed class PollerEntry(int index, NpgsqlConnection connection)
    {
        public int Index { get; } = index;

        public NpgsqlConnection Connection { get; } = connection;

        public bool Retired { get; set; }

        public bool DisposalStarted { get; set; }

        public TrackedOperation? Disposal { get; set; }
    }

    private enum LeaseState
    {
        Active,
        Released,
        Disposing,
        Disposed,
        DisposalFailed,
        LateRetained
    }

    /// <summary>
    /// The cancellation sources of one discovery call. Registered before any of its polls, disposed only by cleanup,
    /// and only once its polls and every cancellation callback have terminated.
    /// </summary>
    private sealed class DiscoveryLease
    {
        private readonly TimeSpan acceptance;

        public DiscoveryLease(int id, string owner, CancellationTokenSource budget, CancellationTokenSource linked, TimeSpan acceptance)
        {
            Id = id;
            Owner = owner;
            Budget = budget;
            Linked = linked;
            this.acceptance = acceptance;
            Clock = Stopwatch.StartNew();
        }

        public int Id { get; }

        public string Owner { get; }

        public CancellationTokenSource Budget { get; }

        public CancellationTokenSource Linked { get; }

        public Stopwatch Clock { get; }

        // Guarded by the orchestrator lock.
        public List<AdmittedOperation> Polls { get; } = [];

        // Guarded by the orchestrator lock.
        public LeaseState State { get; set; } = LeaseState.Active;

        public TrackedOperation? Disposal { get; set; }

        public TimeSpan Remaining => acceptance - Clock.Elapsed;

        public bool Expired => Budget.IsCancellationRequested || Clock.Elapsed >= acceptance;
    }

    /// <summary>
    /// A cancellation source owned by the orchestrator; the source itself never leaves this wrapper. The first request
    /// is the only one: its task is stored, under the wrapper lock, before anything fallible runs and is never replaced,
    /// so the scenario and cleanup always await and inventory that same task. Disposal takes the same lock, is refused
    /// while that task is unresolved, and is attempted at most once; only a Dispose that returned normally is success.
    /// The wrapper lock is a leaf: nothing here acquires the orchestrator lock or waits for a callback.
    /// </summary>
    private sealed class OwnedCancellation
    {
        private readonly Lock gate = new();
        private readonly CancellationTokenSource source = new();
        private readonly ConcurrentQueue<string> diagnostics;

        // Written once, under gate; read lock-free.
        private Task? requested;

        // The first observer failure only; written lock-free, never overwritten.
        private Exception? observationFailure;

        // Guarded by gate. The observer continuation of the original request, retained for cleanup accounting.
        private Task? observer;

        // Guarded by gate. Set before the single disposal attempt, whatever its outcome.
        private bool disposalAttempted;
        private Exception? disposalFailure;

        public OwnedCancellation(string name, ConcurrentQueue<string> diagnostics)
        {
            Name = name;
            this.diagnostics = diagnostics;
            Token = source.Token;
        }

        public string Name { get; }

        // Captured at construction, so reading it never touches a disposed source.
        public CancellationToken Token { get; }

        // The original cancellation task, or null if cancellation was never requested.
        public Task? Requested => Volatile.Read(ref requested);

        // The retained observer, or null when cancellation was never requested or registration failed.
        public Task? Observer
        {
            get
            {
                lock (gate)
                {
                    return observer;
                }
            }
        }

        // The scenario's request. Like CancellationTokenSource.CancelAsync it throws once disposal was attempted;
        // otherwise it returns the original task, which every concurrent or later request receives too.
        public Task CancelAsync()
        {
            return Request(throwIfDisposed: true);
        }

        // Cleanup's request: never throws, and returns the same original task.
        public Task RequestCancellation()
        {
            return Request(throwIfDisposed: false);
        }

        // Clean only when cancellation was never requested, or its observer was registered, ran to completion and
        // recorded no failure. Unregistered, unfinished or failed is never clean, whatever the original task did.
        public bool TryConfirmObservation(out string? problem, out Exception? failure)
        {
            lock (gate)
            {
                problem = null;
                failure = Volatile.Read(ref observationFailure);

                if (requested is null)
                {
                    return true;
                }

                if (failure is not null)
                {
                    problem = $"{Name}: cancellation observer failed";

                    return false;
                }

                if (observer is null)
                {
                    problem = $"{Name}: cancellation observer was never registered";

                    return false;
                }

                if (!observer.IsCompleted)
                {
                    problem = $"{Name}: cancellation observer still {observer.Status}";

                    return false;
                }

                if (observer.Status != TaskStatus.RanToCompletion)
                {
                    failure = observer.Exception?.GetBaseException();
                    problem = $"{Name}: cancellation observer ended {observer.Status}";

                    return false;
                }

                return true;
            }
        }

        // True only when Dispose ran and returned normally. Refused while the original cancellation is unresolved.
        // Never retried: a failed attempt keeps returning false with its original exception instance.
        public bool TryDispose(out string? blocker, out Exception? failure)
        {
            lock (gate)
            {
                blocker = null;
                failure = null;

                if (disposalAttempted)
                {
                    if (disposalFailure is { } previous)
                    {
                        failure = previous;
                        blocker = $"{Name}: disposal already failed";

                        return false;
                    }

                    return true;
                }

                if (requested is { IsCompleted: false } running)
                {
                    blocker = $"{Name}: original cancellation still {running.Status}";

                    return false;
                }

                disposalAttempted = true;

                try
                {
                    source.Dispose();
                }
                catch (Exception error)
                {
                    disposalFailure = error;
                    failure = error;
                    blocker = $"{Name}: disposal failed";

                    return false;
                }

                return true;
            }
        }

        private Task Request(bool throwIfDisposed)
        {
            Task first;

            lock (gate)
            {
                if (disposalAttempted)
                {
                    if (throwIfDisposed)
                    {
                        throw new ObjectDisposedException(nameof(CancellationTokenSource));
                    }

                    return requested ?? Task.FromException(new ObjectDisposedException(nameof(CancellationTokenSource)));
                }

                if (requested is { } existing)
                {
                    return existing;
                }

                // CancelAsync transitions the token synchronously and runs every callback on the thread pool, so
                // nothing waits for a callback here. The task is stored before any fallible bookkeeping.
                try
                {
                    first = source.CancelAsync();
                }
                catch (Exception error)
                {
                    first = Task.FromException(error);
                }

                Volatile.Write(ref requested, first);

                // Registered before the gate is released: any request or accounting that passes through the gate after
                // this point sees the observer. Registration only queues a continuation; nothing runs under the gate.
                Observe(first);
            }

            return first;
        }

        // Called under gate. Registration only: the continuation runs on the thread pool, takes no lock and never changes
        // the outcome. A failure to register is recorded; the original task is already stored and is never replaced.
        private void Observe(Task task)
        {
            try
            {
                observer = task.ContinueWith(
                    static (finished, state) => ((OwnedCancellation)state!).RecordOutcome(finished),
                    this,
                    CancellationToken.None,
                    TaskContinuationOptions.None,
                    TaskScheduler.Default);
            }
            catch (Exception failure)
            {
                Interlocked.CompareExchange(ref observationFailure, failure, null);
            }
        }

        // Reads the exception, so the fault is observed, and records it. The body cannot throw, so the retained
        // continuation always runs to completion; a failure here is kept for cleanup accounting.
        private void RecordOutcome(Task finished)
        {
            try
            {
                var error = finished.Exception;

                if (error is not null)
                {
                    var cause = error.GetBaseException();
                    diagnostics.Enqueue($"Observed fault of {Name} cancellation: {cause.GetType().Name}: {cause.Message}");
                }
            }
            catch (Exception failure)
            {
                Interlocked.CompareExchange(ref observationFailure, failure, null);
            }
        }
    }

    private sealed record ActivityRow(
        int ProcessId,
        string Database,
        string ApplicationName,
        string State,
        string WaitEventType,
        string WaitEvent,
        string Query,
        int[] BlockedBy,
        string BackendStart)
    {
        public override string ToString()
        {
            return $"pid={ProcessId} db={Database} app={ApplicationName} state={State} wait={WaitEventType}/{WaitEvent} "
                + $"blockedBy=[{string.Join(",", BlockedBy)}] start={BackendStart} query={Query}";
        }
    }

    private sealed record ResourceSnapshot(
        Caller[] Callers,
        PersistenceTestHost[] OrphanHosts,
        Holder[] Holders,
        Holder[] LateHolders,
        AdmittedOperation[] Admitted,
        DiscoveryLease[] Leases);

    /// <summary>
    /// Owns one scenario's sessions and resources: caller hosts, holders, the read-only poller, discovery leases and
    /// the scenario's cancellation sources. Every collection is read and written under one lock, never across an await;
    /// once cleanup begins nothing new is admitted, and only resources proven idle are released.
    /// </summary>
    private sealed class Orchestrator
    {
        private static readonly TimeSpan CancelPhase = TimeSpan.FromSeconds(5);
        private static readonly TimeSpan ScenarioUnwindPhase = TimeSpan.FromSeconds(10);
        private static readonly TimeSpan ScenarioOperationPhase = TimeSpan.FromSeconds(10);
        private static readonly TimeSpan SideEffectDrainPhase = TimeSpan.FromSeconds(15);
        private static readonly TimeSpan HolderAcquirePhase = TimeSpan.FromSeconds(5);
        private static readonly TimeSpan HolderRollbackPhase = TimeSpan.FromSeconds(10);
        private static readonly TimeSpan HolderClosePhase = TimeSpan.FromSeconds(5);
        private static readonly TimeSpan CallerDrainPhase = TimeSpan.FromSeconds(30);
        private static readonly TimeSpan DisposalPhase = TimeSpan.FromSeconds(10);
        private static readonly TimeSpan PollerAcquirePhase = TimeSpan.FromSeconds(5);
        private static readonly TimeSpan DiagnosticsBudget = TimeSpan.FromSeconds(5);
        private static readonly TimeSpan MainWriteTimeout = TimeSpan.FromSeconds(15);
        private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(25);

        private readonly string baseConnectionString;
        private readonly string runId = Guid.NewGuid().ToString("n");
        private readonly OwnedCancellation cancellation;
        private readonly Lock sync = new();
        private readonly SemaphoreSlim pollerGate = new(1, 1);
        private readonly ConcurrentQueue<string> diagnostics = new();

        // The original exceptions behind every cleanup failure that has one, kept as thrown.
        private readonly ConcurrentQueue<Exception> cleanupErrors = new();

        // Interlocked; it only ever increases, so a phase-processing failure can never be erased.
        private int phaseProcessingFailures;

        // Guarded by sync.
        private readonly List<Caller> callers = [];
        private readonly List<PersistenceTestHost> orphanHosts = [];
        private readonly List<Holder> holders = [];
        private readonly List<Holder> lateHolders = [];
        private readonly List<PendingCreation> pendingCreations = [];
        private readonly List<OwnedCancellation> scenarioSources = [];
        private readonly List<AdmittedOperation> admitted = [];
        private readonly List<PollerEntry> pollerInventory = [];
        private readonly List<DiscoveryLease> discoveryLeases = [];
        private readonly List<TrackedOperation> cancelOperations = [];
        private readonly List<string> retained = [];
        private bool stopping;
        private bool snapshotTaken;
        private bool pollerTerminal;
        private int nextHolderId;
        private int nextLeaseId;
        private int nextPollerIndex;
        private int incompleteFinalizations;
        private long nextOperationId;

        // Guarded by pollerGate.
        private PollerEntry? currentPoller;

        public Orchestrator(string baseConnectionString)
        {
            this.baseConnectionString = baseConnectionString;
            cancellation = new OwnedCancellation("scenario token", diagnostics);
            DatabaseName =new NpgsqlConnectionStringBuilder(baseConnectionString).Database
                ?? throw new InvalidOperationException("The test connection string names no database.");
        }

        public CancellationToken Token => cancellation.Token;

        public string DatabaseName { get; }

        public Caller CreateCaller(string name, int lockTimeoutMilliseconds = 20_000)
        {
            lock (sync)
            {
                EnsureRunning($"create caller {name}");

                var role = $"c-{name}";
                var connectionString = SessionConnectionString(
                    role,
                    pooling: true,
                    $"-c lock_timeout={lockTimeoutMilliseconds} -c statement_timeout=25000",
                    commandTimeoutSeconds: 30);
                var interceptor = new SessionInterceptor(name, WriterConnectionString(name), Token);

                // Building the host and the scope performs no I/O, so it may happen under the lock.
                var host = PersistenceTestHost.Create(connectionString, interceptor);

                try
                {
                    var caller = new Caller(this, name, ApplicationName(role), host, interceptor);
                    callers.Add(caller);

                    return caller;
                }
                catch
                {
                    orphanHosts.Add(host);
                    throw;
                }
            }
        }

        public async Task<Holder> BeginHolderAsync()
        {
            PendingCreation? creation = null;

            return await Admit<Holder>(
                "holder creation",
                OperationKind.HolderCreation,
                owner: null,
                start: () => CreateHolderAsync(creation),
                onAdmittedUnderLock: _ =>
                {
                    // Everything that can throw happens before the record is registered; nothing after can throw.
                    var id = nextHolderId + 1;
                    var pending = new PendingCreation(id);
                    pendingCreations.Add(pending);
                    nextHolderId = id;
                    creation = pending;
                },
                onNotStarted: failure =>
                {
                    if (creation is { } pending)
                    {
                        FinalizeCreation(pending, holder: null, failure);
                    }
                });
        }

        // The source never leaves its wrapper, and the wrapper is registered before it is returned.
        public OwnedCancellation CreateCancellation()
        {
            lock (sync)
            {
                EnsureRunning("create cancellation source");
                var source = new OwnedCancellation($"source #{scenarioSources.Count + 1}", diagnostics);

                try
                {
                    scenarioSources.Add(source);
                }
                catch
                {
                    // Never registered, exposed or requested: this call alone owns it. A failed disposal is an
                    // incomplete finalization (already under sync), so cleanup can never report success over it.
                    if (!source.TryDispose(out _, out var disposalFailure) && disposalFailure is not null)
                    {
                        incompleteFinalizations++;
                        cleanupErrors.Enqueue(disposalFailure);
                    }

                    throw;
                }

                return source;
            }
        }

        // A write owned by the scenario's main flow, admitted and tracked, on its own unpooled connection.
        public async Task WriteAsync(string sql, params (string Name, object Value)[] parameters)
        {
            var token = Token;

            await Admit(
                "main write",
                OperationKind.MainWrite,
                owner: null,
                async () =>
                {
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                    timeout.CancelAfter(MainWriteTimeout);
                    await ExecuteOnAsync(WriterConnectionString("main"), sql, timeout.Token, parameters);

                    return true;
                });
        }

        // The observer is attached to the original task, including a Task.WhenAll aggregate, before the bounded wrapper
        // can return. Observing a fault proves nothing about completion and never alters the awaited outcome.
        public Task<T> AwaitAsync<T>(Task<T> task)
        {
            ArgumentNullException.ThrowIfNull(task);
            ObserveFaults(task, "awaited operation", diagnostics);

            return task.WaitAsync(OperationWait, Token);
        }

        public async Task DisarmAsync()
        {
            var snapshot = Snapshot();
            var drains = snapshot.Callers
                .Select(caller => new TrackedOperation($"{caller.Name} side-effect drain", StartSafely(caller.Interceptor.Gate.BeginStop)))
                .ToList();

            if (!await RunPhaseSafeAsync("disarm", SideEffectDrainPhase, drains, faultsAreFailures: true))
            {
                throw new InvalidOperationException(
                    "Interceptor side effects did not drain before verification:" + Environment.NewLine + OutstandingSideEffects(snapshot.Callers));
            }
        }

        /// <summary>
        /// Waits until every waiter's UPDATE is observed waiting on a lock in this private database under its own
        /// application name and PID, directly or transitively behind the holder. An observation is accepted only if it
        /// completed within the frozen 10-second budget and the orchestrator was not cancelled.
        /// </summary>
        public async Task WaitUntilBlockedAsync(Holder holder, params Caller[] waiters)
        {
            var lease = CreateLease("WaitUntilBlocked");
            var stage = "readiness";

            try
            {
                foreach (var waiter in waiters)
                {
                    var operation = waiter.LatestOperation
                        ?? throw new InvalidOperationException($"{waiter.Name} has not started an operation.");
                    var remaining = lease.Remaining;

                    if (remaining <= TimeSpan.Zero)
                    {
                        throw new DiscoveryStageEndedException(stage);
                    }

                    var delay = Task.Delay(remaining, lease.Linked.Token);
                    var first = await Task.WhenAny(waiter.Interceptor.UpdateStarted, operation, delay);

                    Token.ThrowIfCancellationRequested();

                    if (first == operation)
                    {
                        throw new InvalidOperationException(await DescribeAsync(
                            $"{waiter.Name} ended ({operation.Status}) before its UPDATE reached the intended lock boundary."));
                    }

                    if (first == delay)
                    {
                        throw new DiscoveryStageEndedException(stage);
                    }

                    if (waiter.Interceptor.UpdateStarted.IsCanceled)
                    {
                        throw new InvalidOperationException($"{waiter.Name}: the readiness barrier was released without an UPDATE.");
                    }
                }

                while (true)
                {
                    stage = "poll";
                    Token.ThrowIfCancellationRequested();

                    var activity = await ObserveAsync(lease);

                    Token.ThrowIfCancellationRequested();

                    if (lease.Expired)
                    {
                        throw new DiscoveryStageEndedException("late observation");
                    }

                    foreach (var waiter in waiters)
                    {
                        var operation = waiter.LatestOperation!;

                        if (operation.IsCompleted)
                        {
                            throw new InvalidOperationException(await DescribeAsync(
                                $"{waiter.Name} ended ({operation.Status}) before the blocker relationship was established."));
                        }
                    }

                    if (waiters.All(waiter => IsBlockedBy(waiter, holder.ProcessId, activity)))
                    {
                        return;
                    }

                    stage = "pause";
                    var pause = lease.Remaining;

                    if (pause <= TimeSpan.Zero)
                    {
                        throw new DiscoveryStageEndedException(stage);
                    }

                    await Task.Delay(pause < PollInterval ? pause : PollInterval, lease.Linked.Token);
                }
            }
            catch (Exception error) when (error is not ScenarioStoppedException)
            {
                var classified = await ClassifyAsync(error is DiscoveryStageEndedException ? null : error, lease, stage);
                ExceptionDispatchInfo.Capture(classified).Throw();
                throw;
            }
            finally
            {
                ReleaseLease(lease);
            }
        }

        public async Task WaitUntilNoLongerWaitingAsync(Caller caller)
        {
            var processId = caller.Interceptor.LastUpdateProcessId
                ?? throw new InvalidOperationException($"{caller.Name} never attempted an UPDATE.");
            var lease = CreateLease("WaitUntilNoLongerWaiting");
            var stage = "poll";

            try
            {
                while (true)
                {
                    stage = "poll";
                    Token.ThrowIfCancellationRequested();

                    var activity = await ObserveAsync(lease);

                    Token.ThrowIfCancellationRequested();

                    if (lease.Expired)
                    {
                        throw new DiscoveryStageEndedException("late observation");
                    }

                    var row = activity.SingleOrDefault(candidate => candidate.ProcessId == processId);

                    if (row is null || row.State != "active" || row.WaitEventType != "Lock")
                    {
                        return;
                    }

                    stage = "pause";
                    var pause = lease.Remaining;

                    if (pause <= TimeSpan.Zero)
                    {
                        throw new DiscoveryStageEndedException(stage);
                    }

                    await Task.Delay(pause < PollInterval ? pause : PollInterval, lease.Linked.Token);
                }
            }
            catch (Exception error) when (error is not ScenarioStoppedException)
            {
                var classified = await ClassifyAsync(error is DiscoveryStageEndedException ? null : error, lease, stage);
                ExceptionDispatchInfo.Capture(classified).Throw();
                throw;
            }
            finally
            {
                ReleaseLease(lease);
            }
        }

        // Never throws: server diagnostics are supplemental and bounded by their own budget.
        public async Task<string> DescribeAsync(string message)
        {
            var text = new StringBuilder(message).AppendLine();
            text.AppendLine($"Private database: {DatabaseName}; run: {runId}");

            try
            {
                text.Append(await CollectServerDiagnosticsAsync());
            }
            catch (Exception failure)
            {
                text.AppendLine($"  (server diagnostics unavailable: {failure.GetType().Name}: {failure.Message})");
            }

            var snapshot = Snapshot();

            foreach (var holder in snapshot.Holders.Concat(snapshot.LateHolders))
            {
                text.AppendLine($"  holder #{holder.Id} pid={holder.ProcessId} state={holder.State} ended={holder.Ended}");
            }

            foreach (var caller in snapshot.Callers)
            {
                text.AppendLine($"  caller {caller.Name} app={caller.ApplicationName}");

                foreach (var record in caller.Interceptor.Records())
                {
                    text.AppendLine($"    command {record}");
                }

                foreach (var operation in snapshot.Admitted.Where(operation => ReferenceEquals(operation.Owner, caller)))
                {
                    text.AppendLine($"    operation {operation}");
                }

                foreach (var ticket in caller.Interceptor.Gate.Outstanding())
                {
                    text.AppendLine($"    side effect in flight {ticket}");
                }
            }

            return text.ToString();
        }

        /// <summary>
        /// Bounded cleanup using only test-owned resources. Returns null only when every resource was proved idle and
        /// released; otherwise a diagnostic exception. Unresolved resources are retained, never disposed.
        /// </summary>
        public async Task<Exception?> CleanupAsync(Task scenarioBody)
        {
            var clean = true;

            // 1. Close admission: nothing new can be created or started from here on. Already admitted work stays tracked.
            lock (sync)
            {
                stopping = true;
            }

            // 2. Request cancellation through the real sources; callbacks run asynchronously and are tracked. Wrapper
            // references are snapshotted under the lock, which is released before any cancellation is requested.
            OwnedCancellation[] sources;

            lock (sync)
            {
                sources = [.. scenarioSources];
            }

            var cancels = new List<TrackedOperation>();

            foreach (var source in sources.Prepend(cancellation))
            {
                // Never throws, and returns the wrapper's original task: the same one a concurrent scenario request
                // receives. It is already stored in the wrapper, so a tracking failure below can never hide it.
                var original = source.RequestCancellation();

                try
                {
                    cancels.Add(new TrackedOperation($"cancel {source.Name}", original));
                }
                catch (Exception failure)
                {
                    clean = false;
                    RecordPhaseProcessingFailure($"cancel {source.Name} tracking", failure);
                }
            }

            lock (sync)
            {
                cancelOperations.AddRange(cancels);
            }

            clean &= await RunPhaseSafeAsync("cancel", CancelPhase, cancels, faultsAreFailures: true);

            // 2b. Each original request's observer, bounded: an unfinished or faulted observer is never clean.
            var observers = new List<TrackedOperation>();

            foreach (var source in sources.Prepend(cancellation))
            {
                try
                {
                    if (source.Observer is { } observer)
                    {
                        observers.Add(new TrackedOperation($"{source.Name} cancellation observer", observer));
                    }
                }
                catch (Exception failure)
                {
                    clean = false;
                    RecordPhaseProcessingFailure($"{source.Name} observer tracking", failure);
                }
            }

            clean &= await RunPhaseSafeAsync("cancellation observation", CancelPhase, observers, faultsAreFailures: true);

            // Every wrapper's observation is accounted for at most once; unconfirmed is never clean.
            var accountedObservations = new HashSet<OwnedCancellation>();

            bool ObservationsConfirmed()
            {
                var confirmed = true;

                foreach (var source in sources.Prepend(cancellation))
                {
                    if (source.TryConfirmObservation(out var problem, out var failure))
                    {
                        continue;
                    }

                    confirmed = false;

                    if (accountedObservations.Add(source))
                    {
                        if (failure is not null)
                        {
                            RecordPhaseProcessingFailure($"{source.Name} cancellation observation", failure);
                        }
                        else
                        {
                            diagnostics.Enqueue(problem ?? $"{source.Name}: cancellation observation unconfirmed");
                        }
                    }
                }

                return confirmed;
            }

            // 3. The scenario body: terminated only if its task actually completed, never because a wait expired.
            await RunPhaseSafeAsync("scenario unwind", ScenarioUnwindPhase, [new TrackedOperation("scenario body", scenarioBody)], faultsAreFailures: false);
            var bodyTerminated = scenarioBody.IsCompleted;

            if (!bodyTerminated)
            {
                clean = false;
                RecordRetained("scenario body", 1, "still running; every resource it can reach is retained");
            }

            // 4. Admitted holder creations, main writes and polls.
            AdmittedOperation[] scenarioOperations;

            lock (sync)
            {
                scenarioOperations = [.. admitted.Where(operation => operation.Kind != OperationKind.CallerOperation)];
            }

            clean &= await RunPhaseSafeAsync(
                "scenario operation drain",
                ScenarioOperationPhase,
                scenarioOperations.Select(operation => operation.Tracked).ToList(),
                faultsAreFailures: false);

            // 5. The snapshot: every still-pending creation is assigned to late retention in the same critical section.
            ResourceSnapshot snapshot;

            lock (sync)
            {
                foreach (var creation in pendingCreations)
                {
                    if (!creation.Finalized && creation.Disposition == CreationDisposition.Pending)
                    {
                        creation.Disposition = CreationDisposition.LateRetained;
                    }
                }

                snapshotTaken = true;
                snapshot = SnapshotUnderLock();
            }

            // 6. Barriers.
            foreach (var caller in snapshot.Callers)
            {
                caller.Interceptor.ReleaseBarriers();
            }

            // 7. Stop admitting side effects and drain those already admitted.
            var drains = snapshot.Callers
                .Select(caller => new TrackedOperation($"{caller.Name} side-effect drain", StartSafely(caller.Interceptor.Gate.BeginStop)))
                .ToList();

            if (!await RunPhaseSafeAsync("side-effect drain", SideEffectDrainPhase, drains, faultsAreFailures: true))
            {
                clean = false;
                diagnostics.Enqueue("Side effects still in flight:" + Environment.NewLine + OutstandingSideEffects(snapshot.Callers));
            }

            // 8. Holders: stop admission, take exclusive ownership, make Terminal, then tear down step by step.
            foreach (var holder in snapshot.Holders)
            {
                clean &= await ReleaseHolderAsync(holder, cancels);
            }

            if (snapshot.LateHolders.Length > 0)
            {
                clean = false;
                RecordRetained("late holders", snapshot.LateHolders.Length, "created after the cleanup snapshot; never torn down");
            }

            // 9. Caller operations.
            var callerOperations = snapshot.Admitted
                .Where(operation => operation.Kind == OperationKind.CallerOperation)
                .Select(operation => operation.Tracked)
                .ToList();
            clean &= await RunPhaseSafeAsync("caller drain", CallerDrainPhase, callerOperations, faultsAreFailures: false);

            // 10. Callers: only when their operations, side effects and every cancellation callback have terminated.
            var cancellationsTerminated = UnresolvedCancellations(cancels).Count == 0;
            var disposals = new List<TrackedOperation>();

            foreach (var caller in snapshot.Callers)
            {
                var unresolved = snapshot.Admitted.Count(operation => ReferenceEquals(operation.Owner, caller) && !operation.Task.IsCompleted);

                if (unresolved > 0 || !caller.Interceptor.Gate.IsStopped || !cancellationsTerminated)
                {
                    clean = false;
                    RecordRetained(
                        $"caller {caller.Name} host",
                        1,
                        $"{unresolved} unresolved operation(s); side effects stopped: {caller.Interceptor.Gate.IsStopped}; cancellations terminated: {cancellationsTerminated}");
                    continue;
                }

                var disposal = new TrackedOperation($"{caller.Name} dispose", StartSafely(caller.DisposeAsync));
                ObserveFaults(disposal.Task, disposal.Name, diagnostics);
                disposals.Add(disposal);
            }

            foreach (var host in snapshot.OrphanHosts)
            {
                var disposal = new TrackedOperation("orphan host dispose", StartSafely(() => host.DisposeAsync().AsTask()));
                ObserveFaults(disposal.Task, disposal.Name, diagnostics);
                disposals.Add(disposal);
            }

            clean &= await RunPhaseSafeAsync("caller disposal", DisposalPhase, disposals, faultsAreFailures: true);

            // 11. Poller connections.
            clean &= await ReleasePollersAsync(snapshot, cancels);

            // 12. Discovery leases.
            clean &= await ReleaseLeasesAsync(snapshot, cancels);

            // 13. Every failed ownership finalization fails cleanup.
            int incomplete;

            lock (sync)
            {
                incomplete = incompleteFinalizations;
            }

            if (incomplete > 0)
            {
                clean = false;
                diagnostics.Enqueue($"{incomplete} ownership finalization(s) failed.");
            }

            // 13b. Every cancellation observer, then every sticky phase-processing failure: an operation that completed
            // later never erases one.
            if (!ObservationsConfirmed())
            {
                clean = false;
            }

            if (PhaseProcessingFailures() > 0)
            {
                clean = false;
                diagnostics.Enqueue($"{PhaseProcessingFailures()} phase-processing failure(s).");
            }

            // 14. Cancellation sources: only when everything is released, the scenario body has terminated and no phase
            // processing failed. Each wrapper refuses while its original cancellation is unresolved and never retries.
            if (clean && bodyTerminated && PhaseProcessingFailures() == 0)
            {
                foreach (var source in sources.Prepend(cancellation))
                {
                    try
                    {
                        if (!source.TryDispose(out var blocker, out var disposalFailure))
                        {
                            clean = false;

                            if (disposalFailure is not null)
                            {
                                cleanupErrors.Enqueue(disposalFailure);
                                diagnostics.Enqueue(
                                    $"cancellation source disposal failed: {disposalFailure.GetType().Name}: {disposalFailure.Message}");
                            }

                            RecordRetained($"cancellation {source.Name}", 1, blocker ?? "not disposed");
                        }
                    }
                    catch (Exception failure)
                    {
                        clean = false;
                        cleanupErrors.Enqueue(failure);
                        diagnostics.Enqueue($"cancellation source disposal failed: {failure.GetType().Name}: {failure.Message}");
                    }
                }
            }

            // Success is reported only after the sticky count and every observer are read again.
            if (clean && PhaseProcessingFailures() == 0 && ObservationsConfirmed())
            {
                return null;
            }

            string[] retainedSummary;

            lock (sync)
            {
                retainedSummary = [.. retained];
            }

            return new InvalidOperationException(
                "Cleanup did not complete safely; unresolved resources were retained, not disposed."
                + Environment.NewLine
                + "Retained: " + (retainedSummary.Length == 0 ? "none" : string.Join("; ", retainedSummary))
                + Environment.NewLine
                + string.Join(Environment.NewLine, diagnostics),
                OriginalErrors());
        }

        // The original processing and disposal exceptions, as thrown, or null when none was kept.
        private AggregateException? OriginalErrors()
        {
            Exception[] errors = [.. cleanupErrors];

            return errors.Length == 0 ? null : new AggregateException(errors);
        }

        /// <summary>
        /// Registers the operation and observes its completion task before queueing it. Every failure after
        /// registration settles that task, so nothing stays pending forever. Queueing never runs the delegate inline, but
        /// another thread may begin it at any time afterwards, even before this method returns.
        /// </summary>
        public Task<T> Admit<T>(
            string name,
            OperationKind kind,
            object? owner,
            Func<Task<T>> start,
            Action<AdmittedOperation>? onAdmittedUnderLock = null,
            Action<Exception>? onNotStarted = null)
        {
            TaskCompletionSource<T>? completion = null;
            AdmittedOperation? record = null;
            Exception? primary = null;

            lock (sync)
            {
                EnsureRunning(name);

                try
                {
                    completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
                    ObserveFaults(completion.Task, name, diagnostics);
                    record = new AdmittedOperation(++nextOperationId, name, kind, owner, completion.Task);
                    admitted.Add(record);
                    onAdmittedUnderLock?.Invoke(record);

                    var bridgeRecord = record;
                    var bridgeCompletion = completion;
                    var scheduling = new Task<Task>(() => RunBridgeAsync(bridgeRecord, bridgeCompletion, start));
                    ObserveFaults(scheduling, $"{name} (scheduling)", diagnostics);
                    ObserveFaults(scheduling.Unwrap(), $"{name} (bridge)", diagnostics);
                    scheduling.Start(TaskScheduler.Default);
                }
                catch (Exception failure)
                {
                    primary = failure;
                    record?.MarkNotStarted(failure);
                }
            }

            if (primary is null)
            {
                return completion!.Task;
            }

            completion?.TrySetException(primary);

            if (onNotStarted is not null)
            {
                try
                {
                    onNotStarted(primary);
                }
                catch (Exception finalizationFailure)
                {
                    RecordIncompleteFinalization($"{name}: not-started finalization failed", finalizationFailure);
                }
            }

            ExceptionDispatchInfo.Capture(primary).Throw();

            throw primary;
        }

        // One terminal outcome: genuine cancellation stays Canceled; any other failure keeps its original instance.
        private static async Task RunBridgeAsync<T>(AdmittedOperation record, TaskCompletionSource<T> completion, Func<Task<T>> start)
        {
            try
            {
                record.MarkStarted();
                completion.TrySetResult(await start());
            }
            catch (OperationCanceledException cancellationError)
            {
                completion.TrySetCanceled(cancellationError.CancellationToken);
            }
            catch (Exception error)
            {
                completion.TrySetException(error);
            }
        }

        // Passive: reads the exception so it is observed, records it, and never changes the outcome or proves completion.
        private static void ObserveFaults(Task task, string label, ConcurrentQueue<string> sink)
        {
            task.ContinueWith(
                static (finished, state) =>
                {
                    // The body cannot throw, so this continuation can never itself fault unobserved.
                    try
                    {
                        var error = finished.Exception;
                        var (name, queue) = ((string, ConcurrentQueue<string>))state!;

                        if (error is not null)
                        {
                            var cause = error.GetBaseException();
                            queue.Enqueue($"Observed fault of {name}: {cause.GetType().Name}: {cause.Message}");
                        }
                    }
                    catch
                    {
                        // Only the diagnostic text is lost; the observed task's outcome is never changed.
                    }
                },
                (label, sink),
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }

        private static Task StartSafely(Func<Task> start)
        {
            try
            {
                return start();
            }
            catch (Exception error)
            {
                return Task.FromException(error);
            }
        }

        private async Task<Holder> CreateHolderAsync(PendingCreation? creation)
        {
            if (creation is null)
            {
                throw new InvalidOperationException("Holder creation started without its creation record.");
            }

            Holder? holder = null;
            Exception? error = null;

            try
            {
                holder = await Holder.BeginAsync(creation.Id, HolderConnectionString(creation.Id), Token);

                return holder;
            }
            catch (Exception failure)
            {
                error = failure;
                throw;
            }
            finally
            {
                try
                {
                    FinalizeCreation(creation, holder, error);
                }
                catch (Exception finalizationFailure)
                {
                    RecordIncompleteFinalization($"holder creation #{creation.Id}: finalization failed", finalizationFailure);

                    if (holder is not null)
                    {
                        lock (sync)
                        {
                            RetainLateHolderUnderLock(holder, $"holder #{creation.Id} retained after a failed finalization");
                        }
                    }
                }
            }
        }

        private void FinalizeCreation(PendingCreation creation, Holder? holder, Exception? error)
        {
            lock (sync)
            {
                if (creation.Finalized)
                {
                    if (holder is not null)
                    {
                        RetainLateHolderUnderLock(holder, $"holder creation #{creation.Id}: second finalization");
                    }

                    return;
                }

                creation.Finalized = true;

                if (creation.Disposition == CreationDisposition.Pending)
                {
                    creation.Disposition = snapshotTaken ? CreationDisposition.LateRetained : CreationDisposition.Published;
                }

                creation.Holder = holder;
                creation.Error = error;

                if (holder is null)
                {
                    diagnostics.Enqueue($"holder creation #{creation.Id} ended without a holder: {error?.GetType().Name}: {error?.Message}");
                }
                else if (creation.Disposition == CreationDisposition.Published)
                {
                    holders.Add(holder);
                }
                else
                {
                    RetainLateHolderUnderLock(holder, $"holder #{creation.Id} created after the cleanup snapshot; retained, not torn down");
                }
            }
        }

        // Idempotent: a holder is never both published and retained, and never added twice.
        private void RetainLateHolderUnderLock(Holder holder, string reason)
        {
            if (!holders.Contains(holder) && !lateHolders.Contains(holder))
            {
                lateHolders.Add(holder);
            }

            holder.BeginStop();
            diagnostics.Enqueue(reason);
        }

        private void RecordIncompleteFinalization(string context, Exception failure)
        {
            lock (sync)
            {
                incompleteFinalizations++;
            }

            diagnostics.Enqueue($"{context}: {failure.GetType().Name}: {failure.Message}");
        }

        private void RecordRetained(string resource, int count, string reason)
        {
            var entry = $"{resource} x{count} ({reason})";

            lock (sync)
            {
                retained.Add(entry);
            }

            diagnostics.Enqueue("Retained: " + entry);
        }

        private void EnsureRunning(string operation)
        {
            if (stopping)
            {
                throw new ScenarioStoppedException(operation);
            }
        }

        private ResourceSnapshot Snapshot()
        {
            lock (sync)
            {
                return SnapshotUnderLock();
            }
        }

        private ResourceSnapshot SnapshotUnderLock()
        {
            return new ResourceSnapshot(
                [.. callers],
                [.. orphanHosts],
                [.. holders],
                [.. lateHolders],
                [.. admitted],
                [.. discoveryLeases]);
        }

        private async Task<bool> ReleaseHolderAsync(Holder holder, IReadOnlyList<TrackedOperation> cancels)
        {
            holder.BeginStop();

            if (cancels.Any(operation => !operation.Task.IsCompleted))
            {
                RecordRetained($"holder #{holder.Id}", 1, "a cancellation callback is unresolved");

                return false;
            }

            if (!await holder.TryAcquireForTeardownAsync(HolderAcquirePhase))
            {
                RecordRetained($"holder #{holder.Id}", 1, "still in use by a scenario operation");

                return false;
            }

            var keepExclusive = false;
            var succeeded = true;

            try
            {
                if (!holder.Ended && holder.HasTransaction)
                {
                    var rollbackTimeout = new CancellationTokenSource(HolderRollbackPhase - TimeSpan.FromSeconds(1));
                    var rollback = await RunStepAsync($"holder #{holder.Id} rollback", holder.RollbackCoreAsync(rollbackTimeout.Token), HolderRollbackPhase);

                    if (rollback.State == OperationState.Unresolved)
                    {
                        // No further step may start on this connection while its rollback is unresolved.
                        keepExclusive = true;
                        RecordRetained($"holder #{holder.Id}", 1, "rollback unresolved");

                        return false;
                    }

                    rollbackTimeout.Dispose();

                    if (rollback.State != OperationState.Completed)
                    {
                        // The rollback ended unsuccessfully, so the connection is idle: an explicit, observed close fallback.
                        succeeded = false;
                        var close = await RunStepAsync($"holder #{holder.Id} close fallback", holder.CloseCoreAsync(), HolderClosePhase);

                        if (close.State == OperationState.Unresolved)
                        {
                            keepExclusive = true;
                            RecordRetained($"holder #{holder.Id}", 1, "close fallback unresolved");

                            return false;
                        }

                        succeeded &= close.State == OperationState.Completed;
                    }
                }

                var transactionDisposal = await RunStepAsync($"holder #{holder.Id} transaction dispose", holder.DisposeTransactionCoreAsync(), DisposalPhase);

                if (transactionDisposal.State == OperationState.Unresolved)
                {
                    keepExclusive = true;
                    RecordRetained($"holder #{holder.Id}", 1, "transaction disposal unresolved");

                    return false;
                }

                succeeded &= transactionDisposal.State == OperationState.Completed;

                // Attempted after any terminated transaction disposal, including a faulted one.
                var connectionDisposal = await RunStepAsync($"holder #{holder.Id} connection dispose", holder.DisposeConnectionCoreAsync(), DisposalPhase);

                if (connectionDisposal.State == OperationState.Unresolved)
                {
                    keepExclusive = true;
                    RecordRetained($"holder #{holder.Id}", 1, "connection disposal unresolved");

                    return false;
                }

                return succeeded && connectionDisposal.State == OperationState.Completed;
            }
            catch (Exception unexpected)
            {
                // The state of the connection is unknown: keep exclusive ownership rather than risk overlap.
                keepExclusive = true;
                diagnostics.Enqueue($"holder #{holder.Id} teardown failed unexpectedly: {unexpected.GetType().Name}: {unexpected.Message}");
                RecordRetained($"holder #{holder.Id}", 1, "teardown failed unexpectedly");

                return false;
            }
            finally
            {
                if (!keepExclusive)
                {
                    holder.ReleaseTeardown();
                }
            }
        }

        private async Task<TrackedOperation> RunStepAsync(string name, Task task, TimeSpan deadline)
        {
            var step = new TrackedOperation(name, task);
            ObserveFaults(task, name, diagnostics);
            await RunPhaseSafeAsync(name, deadline, [step], faultsAreFailures: true);

            return step;
        }

        private async Task<bool> ReleasePollersAsync(ResourceSnapshot snapshot, IReadOnlyList<TrackedOperation> cancels)
        {
            if (!await pollerGate.WaitAsync(PollerAcquirePhase))
            {
                RecordRetained("poller inventory", PollerInventoryCount(), "the poller gate is still owned by an admitted poll");

                return false;
            }

            PollerEntry[] entries;
            List<string> blockers;

            try
            {
                blockers =
                [
                    .. snapshot.Admitted
                        .Where(operation => operation.Kind == OperationKind.Poll && !operation.Task.IsCompleted)
                        .Select(operation => $"poll {operation} unresolved"),
                    .. cancels.Where(operation => !operation.Task.IsCompleted).Select(operation => $"{operation.Name} unresolved")
                ];

                lock (sync)
                {
                    if (blockers.Count == 0)
                    {
                        pollerTerminal = true;
                    }

                    entries = [.. pollerInventory];
                }
            }
            catch (Exception failure)
            {
                // No disposal has started, so releasing the gate cannot expose a connection being disposed.
                pollerGate.Release();
                diagnostics.Enqueue($"poller disposal prerequisites failed: {failure.GetType().Name}: {failure.Message}");
                RecordRetained("poller inventory", PollerInventoryCount(), "prerequisite evaluation failed");

                return false;
            }

            if (blockers.Count > 0)
            {
                // Declined: release the gate so an already admitted, queued poll is not stranded behind cleanup.
                pollerGate.Release();
                RecordRetained("poller inventory", entries.Length, string.Join("; ", blockers));

                return false;
            }

            // Teardown started: the gate is never released again.
            var disposals = new List<TrackedOperation>(entries.Length);

            foreach (var entry in entries)
            {
                if (entry.DisposalStarted)
                {
                    continue;
                }

                entry.DisposalStarted = true;
                var label = PollerLabel(entry);
                var operation = new TrackedOperation(label, StartDisposal(entry.Connection));
                entry.Disposal = operation;
                ObserveFaults(operation.Task, label, diagnostics);
                disposals.Add(operation);
            }

            if (!await RunPhaseSafeAsync("poller disposal", DisposalPhase, disposals, faultsAreFailures: true))
            {
                diagnostics.Enqueue("poller disposal did not complete cleanly.");
            }

            var completed = disposals.Count(operation => operation.State == OperationState.Completed);
            var faulted = disposals.Count(operation => operation.State == OperationState.Faulted);
            var canceled = disposals.Count(operation => operation.State == OperationState.Canceled);
            var unresolved = disposals.Count(operation => operation.State is OperationState.Unresolved or OperationState.Running);
            diagnostics.Enqueue($"poller disposal: {completed} completed, {faulted} faulted, {canceled} canceled, {unresolved} unresolved");

            if (unresolved > 0)
            {
                RecordRetained("poller connections", unresolved, "disposal unresolved; the poller gate stays held");
            }

            return unresolved == 0 && faulted == 0 && canceled == 0;
        }

        private async Task<bool> ReleaseLeasesAsync(ResourceSnapshot snapshot, IReadOnlyList<TrackedOperation> cancels)
        {
            var succeeded = true;

            foreach (var lease in snapshot.Leases)
            {
                var blockers = new List<string>();
                bool dispose;

                lock (sync)
                {
                    if (lease.State is not (LeaseState.Active or LeaseState.Released))
                    {
                        continue;
                    }

                    if (lease.State == LeaseState.Active)
                    {
                        blockers.Add("discovery still running");
                    }

                    blockers.AddRange(lease.Polls.Where(poll => !poll.Task.IsCompleted).Select(poll => $"poll {poll} unresolved"));
                    blockers.AddRange(cancels.Where(operation => !operation.Task.IsCompleted).Select(operation => $"{operation.Name} unresolved"));

                    dispose = blockers.Count == 0;
                    lease.State = dispose ? LeaseState.Disposing : LeaseState.LateRetained;
                }

                if (!dispose)
                {
                    succeeded = false;
                    RecordRetained($"discovery lease #{lease.Id} ({lease.Owner}) sources", 2, string.Join("; ", blockers));
                    continue;
                }

                var disposal = new TrackedOperation($"discovery lease #{lease.Id} dispose", DisposeLease(lease));
                ObserveFaults(disposal.Task, disposal.Name, diagnostics);
                lease.Disposal = disposal;
                await RunPhaseSafeAsync(disposal.Name, DisposalPhase, [disposal], faultsAreFailures: true);

                lock (sync)
                {
                    lease.State = disposal.State is OperationState.Unresolved or OperationState.Running
                        ? LeaseState.LateRetained
                        : LeaseState.Disposed;
                }

                // A terminated but faulted disposal is recorded and is not a clean success.
                succeeded &= disposal.State == OperationState.Completed;
            }

            return succeeded;
        }

        private static Task DisposeLease(DiscoveryLease lease)
        {
            var errors = new List<Exception>();

            // Linked first: it is registered on the budget and on the orchestrator token.
            try
            {
                lease.Linked.Dispose();
            }
            catch (Exception error)
            {
                errors.Add(error);
            }

            try
            {
                lease.Budget.Dispose();
            }
            catch (Exception error)
            {
                errors.Add(error);
            }

            return errors.Count == 0 ? Task.CompletedTask : Task.FromException(new AggregateException(errors));
        }

        private DiscoveryLease CreateLease(string purpose)
        {
            lock (sync)
            {
                EnsureRunning(purpose);

                CancellationTokenSource? budget = null;
                CancellationTokenSource? linked = null;

                try
                {
                    budget = new CancellationTokenSource(BlockerDiscoveryDeadline);
                    linked = CancellationTokenSource.CreateLinkedTokenSource(Token, budget.Token);
                    var id = nextLeaseId + 1;
                    var lease = new DiscoveryLease(id, $"{purpose}#{id}", budget, linked, BlockerDiscoveryDeadline);
                    discoveryLeases.Add(lease);
                    nextLeaseId = id;

                    return lease;
                }
                catch
                {
                    // These sources never entered the inventory, so this call alone owns them.
                    linked?.Dispose();
                    budget?.Dispose();
                    throw;
                }
            }
        }

        // Never disposes: cleanup is the single disposal owner of every lease.
        private void ReleaseLease(DiscoveryLease lease)
        {
            lock (sync)
            {
                if (lease.State == LeaseState.Active)
                {
                    lease.State = LeaseState.Released;
                }
            }
        }

        // Rule 1: external cancellation wins. Rule 2: an expired acceptance budget is a timeout. Rule 3: anything else
        // is preserved. Diagnostics are supplemental and can never replace the classified cause.
        private async Task<Exception> ClassifyAsync(Exception? original, DiscoveryLease lease, string stage)
        {
            if (Token.IsCancellationRequested)
            {
                return ExternalCancellation(original, stage);
            }

            if (!lease.Expired)
            {
                return original ?? new InvalidOperationException($"Discovery ended without a classified cause at {stage}.");
            }

            string details;

            try
            {
                details = await DescribeAsync($"Discovery budget {BlockerDiscoveryDeadline} expired at {stage}.");
            }
            catch (Exception diagnosticFailure)
            {
                details = $"(diagnostics unavailable: {diagnosticFailure.GetType().Name}: {diagnosticFailure.Message})";
            }

            if (Token.IsCancellationRequested)
            {
                return ExternalCancellation(original, stage);
            }

            return new TimeoutException(
                $"Discovery budget {BlockerDiscoveryDeadline} expired at {stage}." + Environment.NewLine + details,
                original);
        }

        private Exception ExternalCancellation(Exception? original, string stage)
        {
            return original is OperationCanceledException cancellationError && cancellationError.CancellationToken == Token
                ? cancellationError
                : new OperationCanceledException($"Discovery was cancelled externally at {stage}.", original, Token);
        }

        // The poll is admitted and tracked for its whole life; the wrapper responds to external cancellation or the
        // remaining budget without waiting on the I/O, which keeps its gate and connection until it really ends.
        private async Task<IReadOnlyList<ActivityRow>> ObserveAsync(DiscoveryLease lease)
        {
            var remaining = lease.Remaining;

            if (remaining <= TimeSpan.Zero)
            {
                throw new DiscoveryStageEndedException("before poll");
            }

            var token = lease.Linked.Token;
            var poll = Admit(
                "activity read",
                OperationKind.Poll,
                lease,
                () => PollOnceAsync(ReadActivityCoreAsync, token),
                onAdmittedUnderLock: record => lease.Polls.Add(record));

            var external = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var registration = Token.Register(static state => ((TaskCompletionSource)state!).TrySetResult(), external);

            var first = await Task.WhenAny(poll, Task.Delay(remaining), external.Task);

            if (first != poll)
            {
                throw new DiscoveryStageEndedException(first == external.Task ? "external cancellation" : "wrapper expiry");
            }

            return await poll;
        }

        private async Task<string> CollectServerDiagnosticsAsync()
        {
            lock (sync)
            {
                if (stopping)
                {
                    return "  (server diagnostics skipped: the scenario is stopping)" + Environment.NewLine;
                }
            }

            var read = Admit(
                "diagnostics read",
                OperationKind.Poll,
                owner: null,
                async () =>
                {
                    using var budget = new CancellationTokenSource(DiagnosticsBudget);

                    return await PollOnceAsync(
                        async (connection, token) =>
                        {
                            var text = new StringBuilder();

                            foreach (var row in await ReadActivityCoreAsync(connection, token))
                            {
                                text.AppendLine($"  activity {row}");
                            }

                            foreach (var row in await ReadLocksCoreAsync(connection, token))
                            {
                                text.AppendLine($"  lock {row}");
                            }

                            return text.ToString();
                        },
                        budget.Token);
                });

            if (await Task.WhenAny(read, Task.Delay(DiagnosticsBudget)) != read)
            {
                return "  (server diagnostics unavailable: budget expired; the read stays tracked)" + Environment.NewLine;
            }

            return await read;
        }

        // The only method that touches pollerGate. One acquisition covers opening, execution, reading, reader disposal
        // and retirement; the core readers never acquire it themselves.
        private async Task<T> PollOnceAsync<T>(Func<NpgsqlConnection, CancellationToken, Task<T>> core, CancellationToken token)
        {
            await pollerGate.WaitAsync(token);

            try
            {
                lock (sync)
                {
                    if (pollerTerminal)
                    {
                        throw new PollerClosedException();
                    }
                }

                var entry = currentPoller;

                if (entry is null)
                {
                    var connection = new NpgsqlConnection(PollerConnectionString());

                    lock (sync)
                    {
                        entry = new PollerEntry(++nextPollerIndex, connection);
                        pollerInventory.Add(entry);
                    }

                    currentPoller = entry;
                    await connection.OpenAsync(token);
                }

                return await core(entry.Connection, token);
            }
            catch (Exception error) when (error is NpgsqlException or OperationCanceledException or InvalidOperationException or TimeoutException)
            {
                // Never reused; it stays in the inventory and is disposed only by cleanup.
                if (currentPoller is { } broken)
                {
                    broken.Retired = true;
                    currentPoller = null;
                }

                throw;
            }
            finally
            {
                pollerGate.Release();
            }
        }

        private int PollerInventoryCount()
        {
            lock (sync)
            {
                return pollerInventory.Count;
            }
        }

        private static Task StartDisposal(NpgsqlConnection connection)
        {
            try
            {
                return connection.DisposeAsync().AsTask();
            }
            catch (Exception error)
            {
                return Task.FromException(error);
            }
        }

        private static string PollerLabel(PollerEntry entry)
        {
            try
            {
                return $"poller #{entry.Index} pid {entry.Connection.ProcessID} dispose";
            }
            catch (Exception)
            {
                return $"poller #{entry.Index} (pid unavailable) dispose";
            }
        }

        // RunPhaseAsync never throws in practice; this boundary keeps an unexpected failure from escaping cleanup.
        private async Task<bool> RunPhaseSafeAsync(
            string phase,
            TimeSpan deadline,
            IReadOnlyCollection<TrackedOperation> operations,
            bool faultsAreFailures)
        {
            try
            {
                return await RunPhaseAsync(phase, deadline, operations, faultsAreFailures);
            }
            catch (Exception unexpected)
            {
                diagnostics.Enqueue($"{phase}: phase processing failed: {unexpected.GetType().Name}: {unexpected.Message}");

                return false;
            }
        }

        // One finite deadline for the whole phase. A timeout marks what is still running as unresolved; it never
        // implies the operation, a server cancellation or a lock release finished.
        private async Task<bool> RunPhaseAsync(
            string phase,
            TimeSpan deadline,
            IReadOnlyCollection<TrackedOperation> operations,
            bool faultsAreFailures)
        {
            if (operations.Count == 0)
            {
                return true;
            }

            var all = Task.WhenAll(operations.Select(operation => operation.Task));
            await Task.WhenAny(all, Task.Delay(deadline));
            ObserveFaults(all, $"{phase} (aggregate)", diagnostics);

            var succeeded = true;

            foreach (var operation in operations)
            {
                operation.Settle(phase, diagnostics);

                if (operation.State == OperationState.Unresolved)
                {
                    succeeded = false;
                    diagnostics.Enqueue($"{phase}: {operation.Name} still running after {deadline}; retained.");
                }
                else if (faultsAreFailures && operation.State is OperationState.Faulted or OperationState.Canceled)
                {
                    succeeded = false;
                    diagnostics.Enqueue($"{phase}: {operation}");
                }
            }

            return succeeded;
        }

        private static string OutstandingSideEffects(IEnumerable<Caller> callers)
        {
            return string.Join(
                Environment.NewLine,
                callers.SelectMany(caller => caller.Interceptor.Gate.Outstanding().Select(ticket => $"  {caller.Name}: {ticket}")));
        }

        private bool IsBlockedBy(Caller waiter, int holderProcessId, IReadOnlyList<ActivityRow> activity)
        {
            var processId = waiter.Interceptor.LastUpdateProcessId;
            var row = activity.SingleOrDefault(candidate => candidate.ProcessId == processId);

            if (row is null
                || row.Database != DatabaseName
                || row.ApplicationName != waiter.ApplicationName
                || row.State != "active"
                || row.WaitEventType != "Lock"
                || !row.Query.TrimStart().StartsWith("UPDATE", StringComparison.OrdinalIgnoreCase)
                || !row.Query.Contains("notifications.notifications", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            // Directly or through the wait queue: follow pg_blocking_pids until the holder is reached.
            var blockers = activity.ToDictionary(candidate => candidate.ProcessId, candidate => candidate.BlockedBy);
            var visited = new HashSet<int> { row.ProcessId };
            var frontier = new Queue<int>(row.BlockedBy);

            while (frontier.Count > 0)
            {
                var next = frontier.Dequeue();

                if (next == holderProcessId)
                {
                    return true;
                }

                if (visited.Add(next) && blockers.TryGetValue(next, out var upstream))
                {
                    foreach (var blocker in upstream)
                    {
                        frontier.Enqueue(blocker);
                    }
                }
            }

            return false;
        }

        private static async Task<IReadOnlyList<ActivityRow>> ReadActivityCoreAsync(NpgsqlConnection connection, CancellationToken token)
        {
            await using var command = new NpgsqlCommand(
                """
                SELECT a.pid, a.datname, coalesce(a.application_name, ''), coalesce(a.state, ''),
                       coalesce(a.wait_event_type, ''), coalesce(a.wait_event, ''), coalesce(left(a.query, 300), ''),
                       pg_blocking_pids(a.pid), coalesce(a.backend_start::text, '')
                FROM pg_stat_activity AS a
                WHERE a.datname = current_database()
                """,
                connection);
            await using var reader = await command.ExecuteReaderAsync(token);

            var rows = new List<ActivityRow>();

            while (await reader.ReadAsync(token))
            {
                rows.Add(new ActivityRow(
                    reader.GetInt32(0),
                    reader.GetString(1),
                    reader.GetString(2),
                    reader.GetString(3),
                    reader.GetString(4),
                    reader.GetString(5),
                    reader.GetString(6),
                    reader.GetFieldValue<int[]>(7),
                    reader.GetString(8)));
            }

            return rows;
        }

        private static async Task<IReadOnlyList<string>> ReadLocksCoreAsync(NpgsqlConnection connection, CancellationToken token)
        {
            await using var command = new NpgsqlCommand(
                """
                SELECT l.pid, l.locktype, l.mode, l.granted, coalesce(l.relation::regclass::text, '')
                FROM pg_locks AS l
                WHERE l.pid IN (SELECT a.pid FROM pg_stat_activity AS a WHERE a.datname = current_database())
                ORDER BY l.pid
                """,
                connection);
            await using var reader = await command.ExecuteReaderAsync(token);

            var rows = new List<string>();

            while (await reader.ReadAsync(token))
            {
                rows.Add($"pid={reader.GetInt32(0)} {reader.GetString(1)} {reader.GetString(2)} granted={reader.GetBoolean(3)} {reader.GetString(4)}");
            }

            return rows;
        }

        private string PollerConnectionString()
        {
            return SessionConnectionString("p", pooling: false, "-c statement_timeout=5000", commandTimeoutSeconds: 10);
        }

        private string HolderConnectionString(int id)
        {
            return SessionConnectionString(
                $"h{id}",
                pooling: false,
                "-c statement_timeout=25000 -c idle_in_transaction_session_timeout=30000",
                commandTimeoutSeconds: 30);
        }

        private string WriterConnectionString(string name)
        {
            return SessionConnectionString(
                $"w-{name}",
                pooling: false,
                "-c lock_timeout=5000 -c statement_timeout=10000",
                commandTimeoutSeconds: 15);
        }

        private string ApplicationName(string role)
        {
            return $"notif-{runId}-{role}";
        }

        private string SessionConnectionString(string role, bool pooling, string options, int commandTimeoutSeconds)
        {
            return new NpgsqlConnectionStringBuilder(baseConnectionString)
            {
                ApplicationName = ApplicationName(role),
                Pooling = pooling,
                Options = options,
                CommandTimeout = commandTimeoutSeconds,
                CancellationTimeout = 2000
            }.ConnectionString;
        }
    }
}

