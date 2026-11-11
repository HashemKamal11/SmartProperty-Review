using SmartProperty.Application.Abstractions.Identity;
using SmartProperty.Application.Abstractions.Messaging;
using SmartProperty.Application.Abstractions.Persistence.Repositories;
using SmartProperty.Application.Abstractions.Time;
using SmartProperty.Common.Results;
using SmartProperty.Domain.Notifications;

namespace SmartProperty.Application.Notifications.MarkRead;

/// <summary>
/// Marks one of the authenticated user's notifications read. Idempotent: an already read notification succeeds and
/// keeps its first read time. There is deliberately no user or recipient member.
/// </summary>
public sealed record MarkNotificationReadCommand(Guid NotificationId) : ICommand;

/// <summary>
/// Marks one notification read under the scope the gate issued. The read time is the server clock, never a client
/// value. It never commits a unit of work: the repository applies the read-state change itself, and owns making that
/// transition safe under concurrency.
/// </summary>
public sealed class MarkNotificationReadCommandHandler(
    ICurrentUser currentUser,
    IUserRepository users,
    INotificationRepository notifications,
    IDateTimeProvider dateTimeProvider)
    : ICommandHandler<MarkNotificationReadCommand>
{
    public async Task<Result> Handle(
        MarkNotificationReadCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        var authorization = await NotificationRecipientGate.AuthorizeAsync(currentUser, users, cancellationToken);

        if (authorization.IsFailure)
        {
            return Result.Failure(authorization.Error!);
        }

        // No notification can have the empty id, so asking the repository would only confirm what is already known.
        if (command.NotificationId == Guid.Empty)
        {
            return Result.Failure(NotificationErrors.NotFound);
        }

        var readAt = Notification.ToStoragePrecision(dateTimeProvider.UtcNow);

        var outcome = await notifications.MarkReadAsync(
            authorization.Value,
            command.NotificationId,
            readAt,
            cancellationToken);

        return outcome switch
        {
            NotificationReadOutcome.MarkedRead or NotificationReadOutcome.AlreadyRead => Result.Success(),
            NotificationReadOutcome.NotFound => Result.Failure(NotificationErrors.NotFound),
            _ => throw new InvalidOperationException(
                $"The notification repository returned an unsupported read outcome '{outcome}'.")
        };
    }
}
