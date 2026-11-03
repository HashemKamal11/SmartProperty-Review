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
    WorkspaceMembershipUserWorkspace = 2,

    /// <summary>
    /// A workspace can have at most one role with a given name.
    /// </summary>
    WorkspaceRoleName = 3,

    /// <summary>
    /// A permission code must be unique.
    /// </summary>
    PermissionCode = 4,

    /// <summary>
    /// A user's normalized phone number must be unique.
    /// </summary>
    UserPhoneNumber = 5,

    /// <summary>
    /// A place keeps one row per AI service id within its level, so two requests that both fill a level do not
    /// store it twice.
    /// </summary>
    LocationAiId = 6,

    /// <summary>
    /// A user can mark a property as a favorite once; the second mark is the same favorite.
    /// </summary>
    FavoriteProperty = 7,

    /// <summary>
    /// A role can carry a given permission at most once (the role_permissions composite key).
    /// </summary>
    RolePermissionKey = 8,

    /// <summary>
    /// A workspace membership can hold a given role at most once (the workspace_membership_roles composite key).
    /// </summary>
    WorkspaceMembershipRoleKey = 9
}
