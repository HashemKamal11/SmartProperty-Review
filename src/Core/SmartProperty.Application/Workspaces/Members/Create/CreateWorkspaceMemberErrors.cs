using SmartProperty.Common.Results;

namespace SmartProperty.Application.Workspaces.Members.Create;

internal static class CreateWorkspaceMemberErrors
{
    public static readonly Error WorkspaceNotFound = new(
        "workspace_members.workspace_not_found", "The workspace was not found.", ErrorType.NotFound);

    /// <summary>The caller holds no workspace.members.create in any workspace.</summary>
    public static readonly Error NotAWorkspaceAdmin = new(
        "workspace_members.forbidden", "You do not administer a workspace.", ErrorType.Forbidden);

    /// <summary>The caller administers several workspaces, and the request does not say which.</summary>
    public static readonly Error AmbiguousWorkspace = new(
        "workspace_members.ambiguous_workspace",
        "Your account administers more than one workspace, so the workspace cannot be chosen automatically.",
        ErrorType.Conflict);

    /// <summary>The workspace's Member role carries something other than exactly workspace.read, so it is not used.</summary>
    public static readonly Error MemberRoleMisconfigured = new(
        "workspace_members.member_role_misconfigured",
        "The workspace's Member role is not configured safely; no account was created.",
        ErrorType.Conflict);

    /// <summary>Another request provisioned the same role or permission at the same moment; nothing was saved.</summary>
    public static readonly Error ConcurrentProvisioning = new(
        "workspace_members.concurrent_provisioning",
        "The workspace was being set up by another request. Nothing was saved; try again.",
        ErrorType.Conflict);

    public static Error ValidationFailed(string message) =>
        new("workspace_members.validation_failed", message, ErrorType.Validation);
}
