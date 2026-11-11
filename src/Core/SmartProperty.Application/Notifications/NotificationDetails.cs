namespace SmartProperty.Application.Notifications;

/// <summary>
/// One notification as its recipient sees it. The frontend renders the text from <see cref="Type"/> and
/// <see cref="Parameters"/> in the user's language, so no display text is carried.
/// </summary>
/// <remarks>
/// The recipient, the occurrence id and the visibility kind are deliberately absent: they are internal identity and
/// policy, not something the recipient needs. A subject only references a resource and grants no access to it.
/// </remarks>
/// <param name="Id">The notification id.</param>
/// <param name="Type">The stable catalog type key.</param>
/// <param name="SubjectType">The kind of resource referenced, or null when the type has no subject.</param>
/// <param name="SubjectId">The id of the resource referenced, or null when the type has no subject.</param>
/// <param name="Parameters">Display parameters; empty for every current type.</param>
/// <param name="CreatedAt">When the notification was created, in UTC.</param>
/// <param name="ReadAt">When the recipient first read it, in UTC, or null while it is unread.</param>
public sealed record NotificationDetails(
    Guid Id,
    string Type,
    string? SubjectType,
    Guid? SubjectId,
    IReadOnlyDictionary<string, string> Parameters,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ReadAt);

/// <summary>One page of the recipient's notifications and the total that match the same filter.</summary>
public sealed record NotificationPage(IReadOnlyList<NotificationDetails> Items, long TotalCount);
