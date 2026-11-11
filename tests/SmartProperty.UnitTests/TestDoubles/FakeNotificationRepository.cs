using SmartProperty.Application.Abstractions.Persistence.Repositories;
using SmartProperty.Application.Notifications;
using SmartProperty.Domain.Notifications;

namespace SmartProperty.UnitTests.TestDoubles;

/// <summary>
/// An in-memory stand-in that applies the scope the way the real repository must: only the scope's recipient, kinds
/// and types are visible, and anything else is indistinguishable from a missing notification.
/// </summary>
/// <remarks>
/// It proves nothing about PostgreSQL: not in-statement eligibility, isolation, concurrency or overflow. It only lets
/// handler tests observe what a handler asks for. The domain entity exposes no way to set <c>ReadAt</c>, so the fake
/// keeps read state on its own rows.
/// </remarks>
internal sealed class FakeNotificationRepository : INotificationRepository
{
    private readonly List<Row> _rows = [];
    private readonly List<Notification> _tracked = [];

    /// <summary>When set, every method throws it instead of answering.</summary>
    public Exception? ExceptionToThrow { get; set; }

    /// <summary>When set, <see cref="MarkReadAsync"/> answers this whatever is stored (a faulty repository).</summary>
    public NotificationReadOutcome? ForcedReadOutcome { get; set; }

    /// <summary>False simulates the in-statement eligibility check failing: nothing is visible.</summary>
    public bool RecipientEligible { get; set; } = true;

    /// <summary>Notifications tracked by <see cref="AddAsync"/>; never visible to reads, because nothing commits.</summary>
    public IReadOnlyList<Notification> Tracked => _tracked;

    public int CallCount { get; private set; }

    public NotificationVisibilityScope? LastScope { get; private set; }

    public bool? LastUnreadOnly { get; private set; }

    public int? LastSkip { get; private set; }

    public int? LastTake { get; private set; }

    public DateTimeOffset? LastReadAt { get; private set; }

    public IReadOnlyCollection<Guid>? LastNotificationIds { get; private set; }

    public CancellationToken LastCancellationToken { get; private set; }

    /// <summary>Stores a committed notification, optionally already read.</summary>
    public void Seed(Notification notification, DateTimeOffset? readAt = null)
    {
        ArgumentNullException.ThrowIfNull(notification);

        _rows.Add(new Row(
            notification,
            readAt is { } value ? Notification.ToStoragePrecision(value) : null));
    }

    /// <summary>The stored read time of a seeded notification, or null while it is unread.</summary>
    public DateTimeOffset? ReadAtOf(Guid notificationId)
    {
        return _rows.Single(row => row.Notification.Id == notificationId).ReadAt;
    }

    public Task AddAsync(Notification notification, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(notification);
        Record(scope: null, cancellationToken);

        _tracked.Add(notification);

        return Task.CompletedTask;
    }

    public Task<NotificationPage> ListAsync(
        NotificationVisibilityScope scope,
        bool unreadOnly,
        int skip,
        int take,
        CancellationToken cancellationToken = default)
    {
        Record(scope, cancellationToken);
        LastUnreadOnly = unreadOnly;
        LastSkip = skip;
        LastTake = take;

        // Filtered first, so the total and the page agree on what unreadOnly admits.
        var matches = Visible(scope)
            .Where(row => !unreadOnly || row.ReadAt is null)
            .OrderByDescending(row => row.Notification.CreatedAt)
            .ThenByDescending(row => row.Notification.Id)
            .ToArray();

        var items = matches
            .Skip(skip)
            .Take(take)
            .Select(ToDetails)
            .ToArray();

        return Task.FromResult(new NotificationPage(items, matches.Length));
    }

    public Task<int> CountUnreadAsync(NotificationVisibilityScope scope, CancellationToken cancellationToken = default)
    {
        Record(scope, cancellationToken);

        return Task.FromResult(Visible(scope).Count(row => row.ReadAt is null));
    }

    public Task<NotificationReadOutcome> MarkReadAsync(
        NotificationVisibilityScope scope,
        Guid notificationId,
        DateTimeOffset readAt,
        CancellationToken cancellationToken = default)
    {
        Record(scope, cancellationToken);
        LastReadAt = readAt;
        LastNotificationIds = [notificationId];

        if (ForcedReadOutcome is { } forced)
        {
            return Task.FromResult(forced);
        }

        var row = Visible(scope).SingleOrDefault(candidate => candidate.Notification.Id == notificationId);

        if (row is null)
        {
            return Task.FromResult(NotificationReadOutcome.NotFound);
        }

        if (row.ReadAt is not null)
        {
            return Task.FromResult(NotificationReadOutcome.AlreadyRead);
        }

        row.MarkRead(readAt);

        return Task.FromResult(NotificationReadOutcome.MarkedRead);
    }

    public Task<int> MarkDisplayedReadAsync(
        NotificationVisibilityScope scope,
        IReadOnlyCollection<Guid> notificationIds,
        DateTimeOffset readAt,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(notificationIds);
        Record(scope, cancellationToken);
        LastReadAt = readAt;
        LastNotificationIds = notificationIds.ToArray();

        var requested = notificationIds.ToHashSet();
        var marked = 0;

        foreach (var row in Visible(scope)
                     .Where(candidate => candidate.ReadAt is null && requested.Contains(candidate.Notification.Id)))
        {
            row.MarkRead(readAt);
            marked++;
        }

        return Task.FromResult(marked);
    }

    private void Record(NotificationVisibilityScope? scope, CancellationToken cancellationToken)
    {
        CallCount++;
        LastScope = scope ?? LastScope;
        LastCancellationToken = cancellationToken;

        if (ExceptionToThrow is { } exception)
        {
            throw exception;
        }
    }

    private IEnumerable<Row> Visible(NotificationVisibilityScope scope)
    {
        ArgumentNullException.ThrowIfNull(scope);

        if (!RecipientEligible)
        {
            return [];
        }

        return _rows.Where(row =>
            row.Notification.RecipientUserId == scope.RecipientUserId
            && scope.VisibilityKinds.Contains(row.Notification.VisibilityKind)
            && scope.Types.Contains(row.Notification.Type));
    }

    private static NotificationDetails ToDetails(Row row)
    {
        var notification = row.Notification;

        return new NotificationDetails(
            notification.Id,
            notification.Type,
            notification.SubjectType,
            notification.SubjectId,
            notification.Parameters,
            notification.CreatedAt,
            row.ReadAt);
    }

    private sealed class Row(Notification notification, DateTimeOffset? readAt)
    {
        public Notification Notification { get; } = notification;

        public DateTimeOffset? ReadAt { get; private set; } = readAt;

        /// <summary>The contract's rule: never earlier than creation, and only the first read is kept.</summary>
        public void MarkRead(DateTimeOffset serverNow)
        {
            if (ReadAt is not null)
            {
                return;
            }

            var normalized = Notification.ToStoragePrecision(serverNow);

            ReadAt = normalized > Notification.CreatedAt ? normalized : Notification.CreatedAt;
        }
    }
}
