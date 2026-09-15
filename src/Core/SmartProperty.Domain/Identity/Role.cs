namespace SmartProperty.Domain.Identity;

public sealed class Role
{
    private Role()
    {
        Name = string.Empty;
    }

    public Role(Guid id, string name, RoleScope scope, Guid? workspaceId, DateTimeOffset createdAt)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException("Role id must not be empty.", nameof(id));
        }

        ValidateScope(scope, workspaceId);

        Id = id;
        Name = TrimRequired(name, nameof(name));
        Scope = scope;
        WorkspaceId = workspaceId;
        CreatedAt = createdAt;
        UpdatedAt = createdAt;
    }

    public Guid Id { get; private set; }
    public string Name { get; private set; }
    public RoleScope Scope { get; private set; }
    public Guid? WorkspaceId { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }

    public void Rename(string name, DateTimeOffset updatedAt)
    {
        ValidateUpdatedAt(updatedAt);

        Name = TrimRequired(name, nameof(name));
        UpdatedAt = updatedAt;
    }

    private void ValidateUpdatedAt(DateTimeOffset updatedAt)
    {
        if (updatedAt < UpdatedAt)
        {
            throw new ArgumentException(
                "Updated date cannot be earlier than the current updated date.",
                nameof(updatedAt));
        }
    }

    private static void ValidateScope(RoleScope scope, Guid? workspaceId)
    {
        if (!Enum.IsDefined(scope))
        {
            throw new ArgumentOutOfRangeException(nameof(scope), scope, "Role scope is not supported.");
        }

        if (scope == RoleScope.Platform && workspaceId is not null)
        {
            throw new ArgumentException("Platform roles must not belong to a workspace.", nameof(workspaceId));
        }

        if (scope == RoleScope.Workspace && (workspaceId is null || workspaceId == Guid.Empty))
        {
            throw new ArgumentException("Workspace roles must belong to a workspace.", nameof(workspaceId));
        }
    }

    private static string TrimRequired(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("Value must not be empty.", parameterName);
        }

        return value.Trim();
    }
}
