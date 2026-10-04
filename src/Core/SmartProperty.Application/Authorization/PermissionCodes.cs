namespace SmartProperty.Application.Authorization;

/// <summary>
/// The permission codes this application recognizes. One place, one spelling.
/// </summary>
/// <remarks>
/// A permission code is a stable authorization contract (see docs/authorization-model.md): it is compared
/// exactly, it is persisted as <c>Permission.Code</c>, and renaming one means introducing a new code and
/// migrating assignments. Declaring each here keeps the string out of endpoints, handlers, seeds, and tests,
/// so the contract cannot drift between the place that grants it and the place that demands it.
///
/// Codes are added only when an endpoint actually requires one. This is not a speculative catalog of everything
/// the product might eventually authorize.
/// </remarks>
public static class PermissionCodes
{
    /// <summary>Create a canonical platform-global property.</summary>
    public const string PropertyCreate = "property.create";

    public const string PropertyCreateDescription = "Create canonical properties.";

    /// <summary>
    /// Review — approve or reject — a workspace access request. Platform-scoped: reviewing who may enter a
    /// workspace is a platform administration act, so it is never satisfied by anything held inside a workspace.
    /// </summary>
    public const string WorkspaceAccessRequestsReview = "workspace.access_requests.review";

    /// <summary>
    /// Human-readable text stored in <c>Permission.Description</c> when the row is provisioned. Descriptive
    /// only: nothing authorizes on it.
    /// </summary>
    public const string WorkspaceAccessRequestsReviewDescription =
        "Approve or reject workspace access requests.";
}
