namespace SmartProperty.Application.Workspaces;

/// <summary>
/// Names of the workspace roles the application provisions itself.
/// </summary>
/// <remarks>
/// A name is a provisioning key, never an authorization decision: approval finds or creates the role by it, and
/// nothing anywhere branches on it to allow anything. What the role may do is the set of permissions attached to it.
/// </remarks>
public static class WorkspaceRoles
{
    /// <summary>The default role every approved member receives in the workspace they were approved into.</summary>
    public const string MemberRoleName = "Member";
}
