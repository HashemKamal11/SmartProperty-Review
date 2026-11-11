using SmartProperty.Application.Abstractions.Identity;
using SmartProperty.Application.Abstractions.Messaging;
using SmartProperty.Application.Abstractions.Persistence.Repositories;
using SmartProperty.Common.Pagination;
using SmartProperty.Common.Results;

namespace SmartProperty.Application.Notifications.List;

/// <summary>
/// One page of the authenticated user's own notifications, newest first.
/// </summary>
/// <remarks>
/// There is deliberately no user or recipient member: whose notifications to read is never a request input. The
/// handler reads the recipient from the authenticated principal through <see cref="NotificationRecipientGate"/>.
/// </remarks>
/// <param name="Page">One-based page number. Null means page 1; out-of-range values are clamped by <see cref="PageParameters"/>.</param>
/// <param name="PageSize">Page size. Null means 10; clamped like <paramref name="Page"/>.</param>
/// <param name="UnreadOnly">True to list only unread notifications; the total counts only those.</param>
public sealed record GetMyNotificationsQuery(int? Page, int? PageSize, bool UnreadOnly = false)
    : IQuery<PagedList<NotificationDetails>>;

/// <summary>
/// Returns one page of the caller's notifications. Read-only: it saves nothing. Filtering, the filtered total, ordering
/// and paging are the repository's, under the scope the gate issued; the handler adds nothing to the page.
/// </summary>
public sealed class GetMyNotificationsQueryHandler(
    ICurrentUser currentUser,
    IUserRepository users,
    INotificationRepository notifications)
    : IQueryHandler<GetMyNotificationsQuery, PagedList<NotificationDetails>>
{
    public async Task<Result<PagedList<NotificationDetails>>> Handle(
        GetMyNotificationsQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        var authorization = await NotificationRecipientGate.AuthorizeAsync(currentUser, users, cancellationToken);

        if (authorization.IsFailure)
        {
            return Result<PagedList<NotificationDetails>>.Failure(authorization.Error!);
        }

        // PageParameters owns both bounds and checked offset arithmetic, so no caller-controlled value can wrap into a
        // negative or oversized database offset.
        var pageParameters = new PageParameters();

        if (query.Page is { } page)
        {
            pageParameters.PageNumber = page;
        }

        if (query.PageSize is { } pageSize)
        {
            pageParameters.PageSize = pageSize;
        }

        var result = await notifications.ListAsync(
            authorization.Value,
            query.UnreadOnly,
            pageParameters.Offset,
            pageParameters.PageSize,
            cancellationToken);

        return Result<PagedList<NotificationDetails>>.Success(
            PagedList<NotificationDetails>.Create(
                result.Items,
                pageParameters.PageNumber,
                pageParameters.PageSize,
                result.TotalCount));
    }
}
