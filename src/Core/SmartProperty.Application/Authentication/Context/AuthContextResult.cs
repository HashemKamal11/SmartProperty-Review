namespace SmartProperty.Application.Authentication.Context;

public sealed record AuthContextResult(
    AuthContextUser User,
    IReadOnlyList<string> PlatformRoles,
    IReadOnlyList<string> PlatformPermissions,
    IReadOnlyList<WorkspaceAuthContext> Workspaces);

public sealed record AuthContextUser(
    Guid Id,
    string Email,
    string FirstName,
    string LastName);

public sealed record WorkspaceAuthContext(
    Guid Id,
    string Name,
    IReadOnlyList<string> Roles,
    IReadOnlyList<string> Permissions);
