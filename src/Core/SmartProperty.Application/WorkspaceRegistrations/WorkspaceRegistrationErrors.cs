using SmartProperty.Common.Results;

namespace SmartProperty.Application.WorkspaceRegistrations;

internal static class WorkspaceRegistrationErrors
{
    public static readonly Error Unauthorized = new(
        "authentication.unauthorized",
        "Authentication is required.",
        ErrorType.Unauthorized);

    public static readonly Error NotFound = new(
        "workspace_registrations.not_found",
        "The workspace registration request was not found.",
        ErrorType.NotFound);

    public static readonly Error PendingRequestExists = new(
        "workspace_registrations.pending_request_exists",
        "You already have a workspace registration request waiting for review.",
        ErrorType.Conflict);

    // The signed-in account cannot apply (it is not Active). Approval has its own error below.
    public static readonly Error AccountNotActive = new(
        "workspace_registrations.account_not_active",
        "Your account must be active to register a workspace.",
        ErrorType.Conflict);

    public static readonly Error AlreadyReviewed = new(
        "workspace_registrations.already_reviewed",
        "This workspace registration request has already been reviewed.",
        ErrorType.Conflict);

    // Approval waits for the applicant to verify the phone number and email instead of skipping the check.
    public static readonly Error ContactNotVerified = new(
        "workspace_registrations.contact_not_verified",
        "The applicant has not verified their phone number and email yet.",
        ErrorType.Conflict);

    // Suspended, Deactivated or missing: one answer for all, and approval never revives a disabled account.
    public static readonly Error ApplicantUnavailable = new(
        "workspace_registrations.applicant_unavailable",
        "The applicant's account cannot be made a workspace admin in its current state.",
        ErrorType.Conflict);

    public static readonly Error ConcurrentChange = new(
        "workspace_registrations.concurrent_change",
        "Another change was saved at the same time. Try again.",
        ErrorType.Conflict);

    public static Error ValidationFailed(string description) =>
        new("validation.failed", description, ErrorType.Validation);
}
