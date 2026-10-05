namespace SmartProperty.Application.Abstractions.Authorization;

/// <summary>
/// Reads the persisted roles and effective permissions visible in a user's platform and workspace contexts.
/// </summary>
public interface IAccessContextReader
{
    Task<AccessContext> ReadAsync(Guid userId, CancellationToken cancellationToken = default);
}

public sealed record AccessContext(
    IReadOnlyList<string> PlatformRoles,
    IReadOnlyList<string> PlatformPermissions,
    IReadOnlyList<WorkspaceAccessContext> Workspaces);

public sealed record WorkspaceAccessContext(
    Guid Id,
    string Name,
    IReadOnlyList<string> Roles,
    IReadOnlyList<string> Permissions);
