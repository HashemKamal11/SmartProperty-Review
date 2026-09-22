using System.Collections.Concurrent;
using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace SmartProperty.Persistence.IntegrationTests.Infrastructure;

/// <summary>
/// Counts the database commands EF Core actually executes, so a test can assert how many round trips one
/// operation costs.
/// </summary>
/// <remarks>
/// Test-only, and installed only on the contexts a test builds for itself. It observes; it never substitutes for
/// the database, changes a command, or short-circuits execution.
///
/// The captured SQL exists for diagnostics — so a failing count says what ran — and is deliberately not asserted
/// on. Pinning generated SQL would break on any EF or Npgsql upgrade that changes it without changing behaviour.
/// </remarks>
internal sealed class DbCommandCounterInterceptor : DbCommandInterceptor
{
    private readonly ConcurrentQueue<string> executedCommands = new();
    private int count;

    /// <summary>Commands executed since the last <see cref="Reset"/>.</summary>
    public int Count => Volatile.Read(ref count);

    /// <summary>The SQL behind <see cref="Count"/>, for diagnostics only.</summary>
    public IReadOnlyList<string> ExecutedCommands => [.. executedCommands];

    /// <summary>Drops everything recorded so far. Called after seeding and before the operation under test.</summary>
    public void Reset()
    {
        Interlocked.Exchange(ref count, 0);
        executedCommands.Clear();
    }

    public override InterceptionResult<DbDataReader> ReaderExecuting(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<DbDataReader> result)
    {
        Record(command);

        return base.ReaderExecuting(command, eventData, result);
    }

    public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<DbDataReader> result,
        CancellationToken cancellationToken = default)
    {
        Record(command);

        return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
    }

    public override InterceptionResult<int> NonQueryExecuting(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<int> result)
    {
        Record(command);

        return base.NonQueryExecuting(command, eventData, result);
    }

    public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        Record(command);

        return base.NonQueryExecutingAsync(command, eventData, result, cancellationToken);
    }

    public override InterceptionResult<object> ScalarExecuting(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<object> result)
    {
        Record(command);

        return base.ScalarExecuting(command, eventData, result);
    }

    public override ValueTask<InterceptionResult<object>> ScalarExecutingAsync(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<object> result,
        CancellationToken cancellationToken = default)
    {
        Record(command);

        return base.ScalarExecutingAsync(command, eventData, result, cancellationToken);
    }

    private void Record(DbCommand command)
    {
        Interlocked.Increment(ref count);
        executedCommands.Enqueue(command.CommandText);
    }
}
