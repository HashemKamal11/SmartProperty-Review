using SmartProperty.Application.Abstractions.Identity;
using SmartProperty.Application.Abstractions.Messaging;
using SmartProperty.Application.Abstractions.Persistence.Repositories;
using SmartProperty.Common.Results;

namespace SmartProperty.Application.Notifications.UnreadCount;

/// <summary>
/// The number of the authenticated user's unread notifications. There is deliberately no user or recipient member.
/// </summary>
public sealed record GetMyUnreadNotificationCountQuery : IQuery<int>;

/// <summary>
/// Returns the caller's unread notification count under the scope the gate issued. Read-only: it saves nothing.
/// </summary>
public sealed class GetMyUnreadNotificationCountQueryHandler(
    ICurrentUser currentUser,
    IUserRepository users,
    INotificationRepository notifications)
    : IQueryHandler<GetMyUnreadNotificationCountQuery, int>
{
    public async Task<Result<int>> Handle(
        GetMyUnreadNotificationCountQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        var authorization = await NotificationRecipientGate.AuthorizeAsync(currentUser, users, cancellationToken);

        if (authorization.IsFailure)
        {
            return Result<int>.Failure(authorization.Error!);
        }

        var count = await notifications.CountUnreadAsync(authorization.Value, cancellationToken);

        // A count can never be negative. A repository that says otherwise is broken, and its answer is not passed on
        // as if it were true.
        if (count < 0)
        {
            throw new InvalidOperationException("The notification repository returned a negative unread count.");
        }

        return Result<int>.Success(count);
    }
}
