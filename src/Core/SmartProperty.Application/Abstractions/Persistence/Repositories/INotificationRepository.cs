using SmartProperty.Application.Notifications;
using SmartProperty.Domain.Notifications;

namespace SmartProperty.Application.Abstractions.Persistence.Repositories;

/// <summary>
/// Persistent in-app notifications. Implemented in Persistence; every read and every read-state change is bounded by a
/// <see cref="NotificationVisibilityScope"/> that only the server builds.
/// </summary>
/// <remarks>
/// <para>
/// The scope is a server-side policy object, not a substitute for database authorization. Every scoped statement must
/// itself enforce, in that same statement: the exact recipient (<see cref="NotificationVisibilityScope.RecipientUserId"/>),
/// that the recipient's account is currently Active, a visibility kind in
/// <see cref="NotificationVisibilityScope.VisibilityKinds"/>, a type in <see cref="NotificationVisibilityScope.Types"/>,
/// and the operation's own filter. The application gate that issued the scope ran earlier, so an account can change
/// status between that gate and a statement; only the in-statement check closes that window, and even it is evaluated
/// against the snapshot that statement sees.
/// </para>
/// <para>
/// A notification the scope does not cover is indistinguishable from one that does not exist: it is never listed,
/// never counted, and reported as <see cref="NotificationReadOutcome.NotFound"/>. When the recipient is not eligible
/// the reads return an empty page and zero, and the read-state changes affect nothing.
/// </para>
/// <para>
/// Separate statements (for example a total count and a page) may observe different committed snapshots under
/// concurrent changes. No coherent snapshot across statements is promised unless an implementation documents and tests
/// an explicit transaction and isolation contract.
/// </para>
/// <para>
/// Read time is always server-derived: callers pass the normalized server clock, never a client value. A notification
/// is marked read with <c>ReadAt = max(normalized readAt, CreatedAt)</c>, so it never reads earlier than it was
/// created, and only while it is unread, so the first read time is never overwritten. Read-state changes are applied
/// by the repository directly; they never commit, flush or discard other tracked changes.
/// </para>
/// <para>
/// Every method passes the cancellation token to the database and propagates failures and cancellation unchanged.
/// </para>
/// </remarks>
public interface INotificationRepository
{
    /// <summary>
    /// Tracks a new notification for insertion. Nothing is saved: the producer's unit of work commits it atomically
    /// with the business change it reports. No scope applies; the producer is trusted server code responsible for
    /// choosing an eligible recipient.
    /// </summary>
    Task AddAsync(Notification notification, CancellationToken cancellationToken = default);

    /// <summary>
    /// One page of the notifications the scope covers, newest first (<c>CreatedAt</c> descending, then <c>Id</c>
    /// descending), with the total that match.
    /// </summary>
    /// <param name="scope">The trusted visibility policy for the current recipient.</param>
    /// <param name="unreadOnly">
    /// True to include only unread notifications. The filter is applied before both the total count and paging, so
    /// <see cref="NotificationPage.TotalCount"/> counts only what the filter admits.
    /// </param>
    /// <param name="skip">Rows to skip, already bounded by the caller's pagination rules.</param>
    /// <param name="take">Rows to return, already bounded by the caller's pagination rules.</param>
    /// <param name="cancellationToken">Passed to every database call.</param>
    Task<NotificationPage> ListAsync(
        NotificationVisibilityScope scope,
        bool unreadOnly,
        int skip,
        int take,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// The number of unread notifications the scope covers.
    /// </summary>
    /// <remarks>
    /// The contract is <see cref="int"/>. An implementation that counts with a wider type must convert with a checked
    /// conversion and let the overflow propagate; it must never wrap or cap the value.
    /// </remarks>
    Task<int> CountUnreadAsync(NotificationVisibilityScope scope, CancellationToken cancellationToken = default);

    /// <summary>
    /// Marks one notification read, if the scope covers it and it is unread.
    /// </summary>
    /// <param name="scope">The trusted visibility policy for the current recipient.</param>
    /// <param name="notificationId">The notification to mark.</param>
    /// <param name="readAt">The normalized server time; the stored value is <c>max(readAt, CreatedAt)</c>.</param>
    /// <param name="cancellationToken">Passed to every database call.</param>
    /// <returns>
    /// Exactly one of the defined <see cref="NotificationReadOutcome"/> values. Telling all three apart reliably under
    /// concurrency is the implementation's responsibility; a row-count from a single update alone does not distinguish
    /// <see cref="NotificationReadOutcome.NotFound"/> from <see cref="NotificationReadOutcome.AlreadyRead"/>.
    /// </returns>
    Task<NotificationReadOutcome> MarkReadAsync(
        NotificationVisibilityScope scope,
        Guid notificationId,
        DateTimeOffset readAt,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Marks read the supplied notifications that the scope covers and that are still unread, and nothing else.
    /// </summary>
    /// <param name="scope">The trusted visibility policy for the current recipient.</param>
    /// <param name="notificationIds">
    /// The notifications the client displayed: distinct, non-empty ids, at most
    /// <see cref="NotificationErrors.MaxDisplayedIds"/>. The caller validates this before calling.
    /// </param>
    /// <param name="readAt">The normalized server time; each stored value is <c>max(readAt, CreatedAt)</c>.</param>
    /// <param name="cancellationToken">Passed to every database call.</param>
    /// <returns>
    /// How many notifications this call changed from unread to read. Ids that are unknown, belong to someone else, are
    /// not covered by the scope, or were already read are not counted.
    /// </returns>
    Task<int> MarkDisplayedReadAsync(
        NotificationVisibilityScope scope,
        IReadOnlyCollection<Guid> notificationIds,
        DateTimeOffset readAt,
        CancellationToken cancellationToken = default);
}

/// <summary>The result of marking one notification read. There is deliberately no zero value.</summary>
public enum NotificationReadOutcome
{
    /// <summary>No notification with that id is covered by the scope: missing, someone else's, or not visible.</summary>
    NotFound = 1,

    /// <summary>The notification was unread and this call marked it read.</summary>
    MarkedRead = 2,

    /// <summary>The notification was already read; its first read time is unchanged.</summary>
    AlreadyRead = 3
}
