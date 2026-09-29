using SmartProperty.Common.Results;

namespace SmartProperty.Application.WorkspaceAccessRequests;

/// <summary>
/// The failures the review use cases return. Shared by approve and reject so one situation has one code
/// whichever review was attempted.
/// </summary>
internal static class WorkspaceAccessRequestErrors
{
    // Matches the code and message the JWT Bearer challenge produces. Normally unreachable: the endpoints
    // require authentication, so this only guards the case where a reviewer id cannot be resolved at all.
    public static readonly Error Unauthorized = new(
        "authentication.unauthorized",
        "Authentication is required.",
        ErrorType.Unauthorized);

    public static readonly Error NotFound = new(
        "workspace_access_requests.not_found",
        "The workspace access request was not found.",
        ErrorType.NotFound);

    // The request exists but is no longer Pending. A second review is a conflict with the decision already
    // recorded, not a validation problem, and it must never silently overwrite the first reviewer's decision.
    public static readonly Error AlreadyReviewed = new(
        "workspace_access_requests.already_reviewed",
        "This workspace access request has already been reviewed.",
        ErrorType.Conflict);

    // Returned when the account named by the request cannot be granted access: it is Suspended or Deactivated,
    // or its row is missing entirely. One error for all of them, so the response does not disclose which, and
    // deliberately a refusal rather than an override — approving a request never revives a disabled account.
    public static readonly Error TargetAccountUnavailable = new(
        "workspace_access_requests.target_account_unavailable",
        "The requesting account cannot be granted workspace access in its current state.",
        ErrorType.Conflict);
}
