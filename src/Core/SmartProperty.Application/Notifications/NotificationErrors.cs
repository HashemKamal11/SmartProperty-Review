using SmartProperty.Application.Authentication.Me;
using SmartProperty.Common.Results;

namespace SmartProperty.Application.Notifications;

internal static class NotificationErrors
{
    /// <summary>The most notification ids one mark-displayed request may name, after duplicates are removed.</summary>
    public const int MaxDisplayedIds = 100;

    /// <summary>No usable identity, or an account that does not exist: the shared account-level <c>401</c>.</summary>
    public static readonly Error Unauthorized = MeErrors.Unauthorized;

    /// <summary>Any non-active account status: the shared account-level <c>403</c>.</summary>
    public static readonly Error AccountUnavailable = MeErrors.AccountUnavailable;

    /// <summary>
    /// The one answer for a notification the caller cannot see, whatever the reason: it does not exist, belongs to
    /// someone else, or is not visible. The caller cannot tell the cases apart.
    /// </summary>
    public static readonly Error NotFound = new(
        "notifications.not_found",
        "The notification was not found.",
        ErrorType.NotFound);

    public static Error ValidationFailed(string description)
    {
        return new Error("notifications.validation_failed", description, ErrorType.Validation);
    }
}
