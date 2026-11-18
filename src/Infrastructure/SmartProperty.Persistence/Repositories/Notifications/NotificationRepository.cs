using Microsoft.EntityFrameworkCore;
using SmartProperty.Application.Abstractions.Persistence.Repositories;
using SmartProperty.Application.Notifications;
using SmartProperty.Domain.Identity;
using SmartProperty.Domain.Notifications;
using SmartProperty.Persistence.Context;

namespace SmartProperty.Persistence.Repositories.Notifications;

/// <summary>
/// Notifications in PostgreSQL. Every scoped statement enforces the scope itself, in SQL: the exact recipient, a
/// visibility kind and a type the scope admits, and that the recipient's account exists and is Active right now.
/// </summary>
/// <remarks>
/// <para>
/// The scope comes from the Application gate, which ran earlier and can be stale by the time a statement runs, so it
/// is never trusted on its own: each statement repeats the eligibility check as a correlated subquery. Under READ
/// COMMITTED that check sees the snapshot of its own statement; a status change committed after a statement began is
/// not visible to that statement.
/// </para>
/// <para>
/// Separate statements (a total and a page; an update and a probe) may observe different committed snapshots. No
/// coherent snapshot across statements is promised, and no explicit transaction is opened here.
/// </para>
/// <para>
/// Read-state changes run immediately as set-based updates. They never call <c>SaveChanges</c>, so they neither commit
/// nor discard other tracked changes, and they do not update entities already tracked by the context: a tracked
/// <see cref="Notification"/> keeps the <c>ReadAt</c> it was loaded with. When a caller has a transaction open, they
/// run inside it, and an outcome returned here does not mean that transaction commits.
/// </para>
/// </remarks>
internal sealed class NotificationRepository(ApplicationDbContext dbContext) : INotificationRepository
{
    /// <summary>
    /// How many update-then-probe rounds <see cref="MarkReadAsync"/> makes before giving up. It bounds the number of
    /// rounds, not the time spent waiting for row locks, which only the command timeout bounds.
    /// </summary>
    internal const int MaxMarkReadAttempts = 3;

    /// <summary>The committed contract's bound on displayed ids per call (Application's NotificationErrors.MaxDisplayedIds).</summary>
    private const int MaxDisplayedIds = 100;

    public async Task AddAsync(Notification notification, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(notification);

        // Tracked only. Nothing is persisted until the caller's unit of work commits.
        await dbContext.Set<Notification>().AddAsync(notification, cancellationToken);
    }

    public async Task<NotificationPage> ListAsync(
        NotificationVisibilityScope scope,
        bool unreadOnly,
        int skip,
        int take,
        CancellationToken cancellationToken = default)
    {
        if (skip < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(skip), skip, "Skip must not be negative.");
        }

        if (take < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(take), take, "Take must be at least 1.");
        }

        var query = Visible(scope).AsNoTracking();

        // Filtered before both the total and the page, so the total counts exactly what the page is drawn from.
        if (unreadOnly)
        {
            query = query.Where(notification => notification.ReadAt == null);
        }

        var totalCount = await query.LongCountAsync(cancellationToken);

        var items = await query
            .OrderByDescending(notification => notification.CreatedAt)
            .ThenByDescending(notification => notification.Id)
            .Skip(skip)
            .Take(take)
            .Select(notification => new NotificationDetails(
                notification.Id,
                notification.Type,
                notification.SubjectType,
                notification.SubjectId,
                notification.Parameters,
                notification.CreatedAt,
                notification.ReadAt))
            .ToListAsync(cancellationToken);

        return new NotificationPage(items, totalCount);
    }

    public async Task<int> CountUnreadAsync(
        NotificationVisibilityScope scope,
        CancellationToken cancellationToken = default)
    {
        var count = await Visible(scope)
            .Where(notification => notification.ReadAt == null)
            .LongCountAsync(cancellationToken);

        // The contract is int. A larger count overflows loudly; it is never wrapped or capped.
        return checked((int)count);
    }

    /// <remarks>
    /// <para>
    /// Each round is a conditional update of the visible unread row, then, only when it changed nothing, a probe of the
    /// same row under the same predicates. The probe distinguishes no visible row (<see cref="NotificationReadOutcome.NotFound"/>),
    /// a visible read row (<see cref="NotificationReadOutcome.AlreadyRead"/>) and a visible unread row. The last means
    /// the two statements saw different states (eligibility changed between them, or the row committed after the
    /// update's snapshot), so the round is repeated; it is never reported as read or as missing.
    /// </para>
    /// <para>
    /// Only that state is retried. Database errors, timeouts and cancellation propagate at once. Cancellation can leave
    /// the outcome uncertain: an update that committed before cancellation was observed stands.
    /// </para>
    /// </remarks>
    public async Task<NotificationReadOutcome> MarkReadAsync(
        NotificationVisibilityScope scope,
        Guid notificationId,
        DateTimeOffset readAt,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);

        var normalizedReadAt = Notification.ToStoragePrecision(readAt);

        for (var attempt = 1; attempt <= MaxMarkReadAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Only an unread row is written, so the first read time is never overwritten, and the stored time is never
            // earlier than the notification's creation.
            var affected = await Visible(scope)
                .Where(notification => notification.Id == notificationId && notification.ReadAt == null)
                .ExecuteUpdateAsync(
                    setters => setters.SetProperty(
                        notification => notification.ReadAt,
                        notification => (DateTimeOffset?)(notification.CreatedAt > normalizedReadAt
                            ? notification.CreatedAt
                            : normalizedReadAt)),
                    cancellationToken);

            if (affected == 1)
            {
                return NotificationReadOutcome.MarkedRead;
            }

            if (affected > 1)
            {
                throw new InvalidOperationException(
                    "Marking one notification read changed more than one row, which the primary key makes impossible.");
            }

            // A row wrapper, so "no visible row" (an empty list) can never be confused with "visible and unread" (a row
            // whose ReadAt is null).
            var probe = await Visible(scope)
                .AsNoTracking()
                .Where(notification => notification.Id == notificationId)
                .Select(notification => new ReadStateProbe(notification.ReadAt))
                .Take(2)
                .ToListAsync(cancellationToken);

            if (probe.Count > 1)
            {
                throw new InvalidOperationException(
                    "Probing one notification returned more than one row, which the primary key makes impossible.");
            }

            if (probe.Count == 0)
            {
                return NotificationReadOutcome.NotFound;
            }

            if (probe[0].ReadAt is not null)
            {
                return NotificationReadOutcome.AlreadyRead;
            }

            // Visible and unread now, though the update did not match it: try again.
        }

        throw new InvalidOperationException(
            $"The notification read state could not be resolved after {MaxMarkReadAttempts} attempts.");
    }

    /// <remarks>
    /// One set-based update. Ids that are unknown, someone else's, not visible or already read are simply not matched,
    /// so the count says only how many notifications this call changed from unread to read.
    /// </remarks>
    public async Task<int> MarkDisplayedReadAsync(
        NotificationVisibilityScope scope,
        IReadOnlyCollection<Guid> notificationIds,
        DateTimeOffset readAt,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(notificationIds);

        if (notificationIds.Count == 0 || notificationIds.Count > MaxDisplayedIds)
        {
            throw new ArgumentException(
                $"Between 1 and {MaxDisplayedIds} notification ids are required.",
                nameof(notificationIds));
        }

        var ids = notificationIds.ToArray();

        if (ids.Any(id => id == Guid.Empty))
        {
            throw new ArgumentException("Notification ids must not be empty.", nameof(notificationIds));
        }

        if (ids.Distinct().Count() != ids.Length)
        {
            throw new ArgumentException("Notification ids must be distinct.", nameof(notificationIds));
        }

        var normalizedReadAt = Notification.ToStoragePrecision(readAt);

        return await Visible(scope)
            .Where(notification => Enumerable.Contains(ids, notification.Id) && notification.ReadAt == null)
            .ExecuteUpdateAsync(
                setters => setters.SetProperty(
                    notification => notification.ReadAt,
                    notification => (DateTimeOffset?)(notification.CreatedAt > normalizedReadAt
                        ? notification.CreatedAt
                        : normalizedReadAt)),
                cancellationToken);
    }

    /// <summary>
    /// The notifications the scope admits, with the recipient's current eligibility checked inside the same statement.
    /// </summary>
    private IQueryable<Notification> Visible(NotificationVisibilityScope scope)
    {
        ArgumentNullException.ThrowIfNull(scope);

        var recipientUserId = scope.RecipientUserId;

        // Plain arrays, so the provider binds each as one array parameter (= ANY) rather than an immutable wrapper.
        var visibilityKinds = scope.VisibilityKinds.ToArray();
        var types = scope.Types.ToArray();

        return dbContext.Set<Notification>()
            .Where(notification =>
                notification.RecipientUserId == recipientUserId
                && Enumerable.Contains(visibilityKinds, notification.VisibilityKind)
                && Enumerable.Contains(types, notification.Type)
                && dbContext.Users.Any(user => user.Id == recipientUserId && user.Status == UserStatus.Active));
    }

    private sealed record ReadStateProbe(DateTimeOffset? ReadAt);
}
