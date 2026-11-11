namespace SmartProperty.Domain.Notifications;

/// <summary>
/// Who may see a notification besides its recipient's own eligibility. The numeric values are persisted, so they never
/// change.
/// </summary>
/// <remarks>
/// Only <see cref="Personal"/> is supported. Workspace-dependent (2) and privileged (3) visibility are reserved for
/// future, separately approved extensions and are deliberately not declared, so no code can create or read them until
/// their authorization rules and tests exist. A stored value that is not declared here must be treated as invisible.
/// </remarks>
public enum NotificationVisibilityKind : short
{
    /// <summary>Visible to its recipient while the recipient's account is eligible, independent of any workspace.</summary>
    Personal = 1
}
