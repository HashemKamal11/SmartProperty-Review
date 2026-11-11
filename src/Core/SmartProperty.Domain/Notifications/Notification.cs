using System.Collections.Immutable;

namespace SmartProperty.Domain.Notifications;

/// <summary>
/// One persistent in-app notification for one recipient. It can only be created through the factory for a type in
/// <see cref="NotificationCatalog"/>, and it never carries secrets, free text, or a workspace name.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="OccurrenceId"/> is the id of the business event the notification reports (a new membership, a consumed
/// password reset grant). It is always the caller's value, never generated here, so the same occurrence always yields
/// the same identity.
/// </para>
/// <para>
/// <see cref="SubjectType"/> and <see cref="SubjectId"/> only reference a resource; they grant no access to it. Every
/// destination authorizes independently.
/// </para>
/// <para>
/// Timestamps are UTC and truncated to whole microseconds, the precision PostgreSQL stores, so a value read back
/// compares equal to the value written.
/// </para>
/// </remarks>
public sealed class Notification
{
    private Notification()
    {
        Type = string.Empty;
        Parameters = ImmutableDictionary<string, string>.Empty;
    }

    private Notification(
        NotificationTypeDefinition definition,
        Guid id,
        Guid recipientUserId,
        Guid occurrenceId,
        Guid? subjectId,
        DateTimeOffset createdAt)
    {
        ArgumentNullException.ThrowIfNull(definition);

        // Defence in depth: the factories only pass catalog singletons, and definitions cannot be built elsewhere.
        if (!NotificationCatalog.TryGet(definition.Type, out var registered) || !ReferenceEquals(registered, definition))
        {
            throw new ArgumentException("The notification type is not in the notification catalog.", nameof(definition));
        }

        if (definition.Visibility != NotificationVisibilityKind.Personal)
        {
            throw new InvalidOperationException("Only personal notifications are supported.");
        }

        if ((definition.SubjectType is null) != (subjectId is null))
        {
            throw new InvalidOperationException("A subject type and a subject id must be present together or not at all.");
        }

        Id = id;
        RecipientUserId = recipientUserId;
        Type = definition.Type;
        OccurrenceId = occurrenceId;
        VisibilityKind = definition.Visibility;
        SubjectType = definition.SubjectType;
        SubjectId = subjectId;
        Parameters = ImmutableDictionary<string, string>.Empty;
        CreatedAt = ToStoragePrecision(createdAt);
        ReadAt = null;
    }

    public Guid Id { get; private set; }

    public Guid RecipientUserId { get; private set; }

    public string Type { get; private set; }

    public Guid OccurrenceId { get; private set; }

    public NotificationVisibilityKind VisibilityKind { get; private set; }

    public string? SubjectType { get; private set; }

    public Guid? SubjectId { get; private set; }

    /// <summary>Display parameters. Read-only and immutable; both current types have none.</summary>
    public IReadOnlyDictionary<string, string> Parameters { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    /// <summary>When the recipient first read the notification, or null while it is unread.</summary>
    public DateTimeOffset? ReadAt { get; private set; }

    /// <summary>
    /// Reports that an existing user was added to a workspace. The occurrence is the new membership. The workspace is
    /// referenced only by id: its name is never stored.
    /// </summary>
    public static Notification CreateMemberAdded(
        Guid id,
        Guid recipientUserId,
        Guid membershipId,
        Guid workspaceId,
        DateTimeOffset createdAt)
    {
        EnsureNotEmpty(id, nameof(id));
        EnsureNotEmpty(recipientUserId, nameof(recipientUserId));
        EnsureNotEmpty(membershipId, nameof(membershipId));
        EnsureNotEmpty(workspaceId, nameof(workspaceId));

        return new Notification(NotificationCatalog.MemberAdded, id, recipientUserId, membershipId, workspaceId, createdAt);
    }

    /// <summary>
    /// Reports that the recipient's password was changed through a password reset. The occurrence is the id of the
    /// consumed reset grant, never its secret token.
    /// </summary>
    public static Notification CreatePasswordChanged(
        Guid id,
        Guid recipientUserId,
        Guid resetGrantId,
        DateTimeOffset createdAt)
    {
        EnsureNotEmpty(id, nameof(id));
        EnsureNotEmpty(recipientUserId, nameof(recipientUserId));
        EnsureNotEmpty(resetGrantId, nameof(resetGrantId));

        return new Notification(NotificationCatalog.PasswordChanged, id, recipientUserId, resetGrantId, subjectId: null, createdAt);
    }

    /// <summary>
    /// Converts a timestamp to UTC and truncates it to whole microseconds, never rounding up. The represented instant
    /// is preserved down to the microsecond, and applying it twice gives the same result.
    /// </summary>
    public static DateTimeOffset ToStoragePrecision(DateTimeOffset value)
    {
        var utc = value.ToUniversalTime();

        return new DateTimeOffset(utc.Ticks - (utc.Ticks % TimeSpan.TicksPerMicrosecond), TimeSpan.Zero);
    }

    private static void EnsureNotEmpty(Guid value, string parameterName)
    {
        if (value == Guid.Empty)
        {
            throw new ArgumentException($"{parameterName} must not be empty.", parameterName);
        }
    }
}
