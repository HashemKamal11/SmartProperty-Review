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
///
/// The one exception is the property authority codes (<c>marketplace.authority.*</c>). They are definitions
/// only: the platform administrator bootstrap neither creates a row for them nor attaches them to any role, no
/// endpoint uses them yet, and nobody holds them. They take effect only when an explicitly authorized assignment
/// workflow provisions them, and until a user is explicitly assigned one, the check for it is a denial. Holding the
/// Platform Admin role, any other permission, or a Workspace role never confers one.
/// </remarks>
public static class PermissionCodes
{
    /// <summary>Create a canonical platform-global property.</summary>
    public const string PropertyCreate = "property.create";

    public const string PropertyCreateDescription = "Create canonical properties.";

    /// <summary>
    /// Read canonical platform-global properties. Platform-scoped: the registry is not owned by a workspace, so
    /// nothing held inside a workspace satisfies it.
    /// </summary>
    public const string PropertyRead = "property.read";

    public const string PropertyReadDescription = "Read canonical properties.";

    /// <summary>
    /// Correct the details of a canonical platform-global property. Platform-scoped for the same reason as
    /// <see cref="PropertyRead"/>: nothing held inside a workspace satisfies it.
    /// </summary>
    public const string PropertyUpdate = "property.update";

    public const string PropertyUpdateDescription = "Update canonical properties.";

    /// <summary>
    /// Archive a canonical platform-global property. Platform-scoped for the same reason as
    /// <see cref="PropertyRead"/>: nothing held inside a workspace satisfies it.
    /// </summary>
    public const string PropertyArchive = "property.archive";

    public const string PropertyArchiveDescription = "Archive canonical properties.";

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

    /// <summary>
    /// Read the marketplace listing submission review queue and submission details. Platform-scoped: reviewing
    /// submissions is a platform administration act, so nothing held inside a workspace satisfies it. It grants
    /// no authority to decide a submission, and <see cref="MarketplaceSubmissionsReviewDecide"/> does not imply it.
    /// </summary>
    public const string MarketplaceSubmissionsReviewRead = "marketplace.submissions.review.read";

    public const string MarketplaceSubmissionsReviewReadDescription =
        "Read the marketplace listing submission review queue and details.";

    /// <summary>
    /// Approve or reject a marketplace listing submission; approval and rejection share this one permission.
    /// Platform-scoped for the same reason as <see cref="MarketplaceSubmissionsReviewRead"/>. It grants no
    /// ability to read the queue, and <see cref="MarketplaceSubmissionsReviewRead"/> does not imply it.
    /// </summary>
    public const string MarketplaceSubmissionsReviewDecide = "marketplace.submissions.review.decide";

    public const string MarketplaceSubmissionsReviewDecideDescription =
        "Approve or reject marketplace listing submissions.";

    /// <summary>
    /// Record an independent legal review for a property authority grant. Platform-scoped and definition-only (see
    /// the remarks on this class). It confers no authority to issue or revoke a grant, to read grants, or to review
    /// listing submissions, and none of those permissions implies it.
    /// </summary>
    public const string MarketplaceAuthorityLegalReviewsRecord = "marketplace.authority.legal_reviews.record";

    public const string MarketplaceAuthorityLegalReviewsRecordDescription =
        "Record legal reviews for property authority grants.";

    /// <summary>
    /// Issue a property authority grant from an approved legal review. Platform-scoped and definition-only. It
    /// confers no authority to record a legal review, to revoke or read grants, or to review listing submissions,
    /// and none of those permissions implies it.
    /// </summary>
    public const string MarketplaceAuthorityGrantsIssue = "marketplace.authority.grants.issue";

    public const string MarketplaceAuthorityGrantsIssueDescription =
        "Issue property authority grants.";

    /// <summary>
    /// Revoke a property authority grant. Platform-scoped and definition-only. It confers no authority to record a
    /// legal review, to issue or read grants, or to review listing submissions, and none of those permissions
    /// implies it.
    /// </summary>
    public const string MarketplaceAuthorityGrantsRevoke = "marketplace.authority.grants.revoke";

    public const string MarketplaceAuthorityGrantsRevokeDescription =
        "Revoke property authority grants.";

    /// <summary>
    /// Read property authority grants and their legal reviews. Platform-scoped and definition-only. It confers no
    /// authority to record, issue or revoke anything, or to review listing submissions, and none of those
    /// permissions implies it.
    /// </summary>
    public const string MarketplaceAuthorityGrantsRead = "marketplace.authority.grants.read";

    public const string MarketplaceAuthorityGrantsReadDescription =
        "Read property authority grants and legal reviews.";

    /// <summary>
    /// Read the workspace itself. Workspace-scoped: it is satisfied only by a workspace role held in that same
    /// workspace, never by a platform role and never by membership of another workspace.
    /// </summary>
    public const string WorkspaceRead = "workspace.read";

    public const string WorkspaceReadDescription = "Read the workspace itself.";
}
