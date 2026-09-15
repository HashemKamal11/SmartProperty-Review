namespace SmartProperty.Domain.Workspaces;

/// <summary>
/// Assigns a workspace-scoped role to a workspace membership. Application workflows must ensure the role belongs to the same workspace as the membership.
/// </summary>
public sealed class WorkspaceMembershipRole
{
    private WorkspaceMembershipRole()
    {
    }

    public WorkspaceMembershipRole(Guid workspaceMembershipId, Guid roleId)
    {
        ValidateRequiredId(workspaceMembershipId, nameof(workspaceMembershipId));
        ValidateRequiredId(roleId, nameof(roleId));

        WorkspaceMembershipId = workspaceMembershipId;
        RoleId = roleId;
    }

    public Guid WorkspaceMembershipId { get; private set; }
    public Guid RoleId { get; private set; }

    private static void ValidateRequiredId(Guid id, string parameterName)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException("Id must not be empty.", parameterName);
        }
    }
}
