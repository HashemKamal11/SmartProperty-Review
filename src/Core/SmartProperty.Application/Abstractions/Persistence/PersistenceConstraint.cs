namespace SmartProperty.Application.Abstractions.Persistence;

/// <summary>
/// Provider-neutral identifiers for database constraints that use cases can handle as expected outcomes.
/// </summary>
public enum PersistenceConstraint
{
    /// <summary>
    /// A user's normalized email must be unique.
    /// </summary>
    UserEmail = 1,

    /// <summary>
    /// A user can have at most one membership in a workspace.
    /// </summary>
    WorkspaceMembershipUserWorkspace = 2
}
