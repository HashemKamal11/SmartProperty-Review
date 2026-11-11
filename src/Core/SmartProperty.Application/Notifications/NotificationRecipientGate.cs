using SmartProperty.Application.Abstractions.Identity;
using SmartProperty.Application.Abstractions.Persistence.Repositories;
using SmartProperty.Common.Results;
using SmartProperty.Domain.Identity;

namespace SmartProperty.Application.Notifications;

/// <summary>
/// Turns the authenticated identity into a <see cref="NotificationVisibilityScope"/>, only after confirming from the
/// persisted account that the caller is still in good standing.
/// </summary>
/// <remarks>
/// The identity is read from <see cref="ICurrentUser"/> exactly once, and that captured value is the only one looked up
/// and placed in the scope. The answers are the established account answers: no usable identity, an account that no
/// longer exists, or an account record that does not match the identity is the same <c>401</c> as no token, so an id is
/// never confirmed real; every non-active status is the one account-level <c>403</c>, so the exact status is not
/// disclosed. A failed lookup, including cancellation, propagates unchanged; it is never an answer.
/// </remarks>
internal static class NotificationRecipientGate
{
    public static async Task<Result<NotificationVisibilityScope>> AuthorizeAsync(
        ICurrentUser currentUser,
        IUserRepository users,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(currentUser);
        ArgumentNullException.ThrowIfNull(users);

        // Captured once: every later step uses this value, never a second read of the current user.
        var capturedUserId = currentUser.UserId;

        if (capturedUserId is not { } recipientUserId || recipientUserId == Guid.Empty)
        {
            return Result<NotificationVisibilityScope>.Failure(NotificationErrors.Unauthorized);
        }

        var account = await users.GetByIdAsync(recipientUserId, cancellationToken);

        // A token can outlive its user, and a record for a different id is never accepted as this caller's account.
        if (account is null || account.Id != recipientUserId)
        {
            return Result<NotificationVisibilityScope>.Failure(NotificationErrors.Unauthorized);
        }

        if (account.Status != UserStatus.Active)
        {
            return Result<NotificationVisibilityScope>.Failure(NotificationErrors.AccountUnavailable);
        }

        return Result<NotificationVisibilityScope>.Success(
            NotificationVisibilityScope.ForVerifiedRecipient(recipientUserId));
    }
}
