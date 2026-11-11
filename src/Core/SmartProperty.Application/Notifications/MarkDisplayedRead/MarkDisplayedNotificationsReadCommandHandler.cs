using SmartProperty.Application.Abstractions.Identity;
using SmartProperty.Application.Abstractions.Messaging;
using SmartProperty.Application.Abstractions.Persistence.Repositories;
using SmartProperty.Application.Abstractions.Time;
using SmartProperty.Common.Results;
using SmartProperty.Domain.Notifications;

namespace SmartProperty.Application.Notifications.MarkDisplayedRead;

/// <summary>
/// Marks read the notifications the client displayed, named explicitly by id. There is deliberately no user or
/// recipient member.
/// </summary>
/// <param name="NotificationIds">
/// The displayed notification ids. Duplicates are ignored; after that there must be between 1 and
/// <see cref="NotificationErrors.MaxDisplayedIds"/> ids, none of them empty.
/// </param>
public sealed record MarkDisplayedNotificationsReadCommand(IReadOnlyCollection<Guid>? NotificationIds)
    : ICommand<MarkDisplayedNotificationsReadResult>;

/// <param name="MarkedCount">
/// How many notifications this call changed from unread to read. It says nothing about which ids exist, belong to the
/// caller, or were already read.
/// </param>
public sealed record MarkDisplayedNotificationsReadResult(int MarkedCount);

/// <summary>
/// Marks the displayed notifications read under the scope the gate issued. The read time is the server clock, never a
/// client value. It never commits a unit of work: the repository applies the read-state change itself.
/// </summary>
/// <remarks>
/// This layer bounds the number of distinct ids, not the raw collection size; limiting the size of the request itself
/// is the API's job.
/// </remarks>
public sealed class MarkDisplayedNotificationsReadCommandHandler(
    ICurrentUser currentUser,
    IUserRepository users,
    INotificationRepository notifications,
    IDateTimeProvider dateTimeProvider)
    : ICommandHandler<MarkDisplayedNotificationsReadCommand, MarkDisplayedNotificationsReadResult>
{
    public async Task<Result<MarkDisplayedNotificationsReadResult>> Handle(
        MarkDisplayedNotificationsReadCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        var authorization = await NotificationRecipientGate.AuthorizeAsync(currentUser, users, cancellationToken);

        if (authorization.IsFailure)
        {
            return Failure(authorization.Error!);
        }

        if (command.NotificationIds is not { } requestedIds)
        {
            return Failure(NotificationErrors.ValidationFailed("At least one notification id is required."));
        }

        // First occurrence wins, so the distinct ids keep the order the client sent them in.
        var seen = new HashSet<Guid>();
        var distinctIds = new List<Guid>();

        foreach (var id in requestedIds)
        {
            if (id == Guid.Empty)
            {
                return Failure(NotificationErrors.ValidationFailed("Notification ids must not be empty."));
            }

            if (!seen.Add(id))
            {
                continue;
            }

            distinctIds.Add(id);

            if (distinctIds.Count > NotificationErrors.MaxDisplayedIds)
            {
                return Failure(NotificationErrors.ValidationFailed(
                    $"At most {NotificationErrors.MaxDisplayedIds} distinct notification ids can be marked at once."));
            }
        }

        if (distinctIds.Count == 0)
        {
            return Failure(NotificationErrors.ValidationFailed("At least one notification id is required."));
        }

        var readAt = Notification.ToStoragePrecision(dateTimeProvider.UtcNow);

        var markedCount = await notifications.MarkDisplayedReadAsync(
            authorization.Value,
            distinctIds.ToArray(),
            readAt,
            cancellationToken);

        // The repository can mark at most the ids it was given. Any other answer is a broken repository; it is reported,
        // not passed on as a result. Whatever such a repository already changed is not undone here.
        if (markedCount < 0 || markedCount > distinctIds.Count)
        {
            throw new InvalidOperationException(
                "The notification repository returned an impossible number of notifications marked read.");
        }

        return Result<MarkDisplayedNotificationsReadResult>.Success(new MarkDisplayedNotificationsReadResult(markedCount));
    }

    private static Result<MarkDisplayedNotificationsReadResult> Failure(Error error)
    {
        return Result<MarkDisplayedNotificationsReadResult>.Failure(error);
    }
}
