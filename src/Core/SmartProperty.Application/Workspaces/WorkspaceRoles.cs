namespace SmartProperty.Application.Workspaces;

public static class WorkspaceRoles
{
    public const string MemberRoleName = "Member";

    /// <summary>
    /// The per-workspace role given to the account created together with a workspace. A provisioning key and a
    /// display name only: nothing authorizes on it, the permissions attached to the role do.
    /// </summary>
    public const string AdminRoleName = "Workspace Admin";
}
