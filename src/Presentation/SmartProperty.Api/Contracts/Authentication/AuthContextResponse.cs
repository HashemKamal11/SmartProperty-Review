#nullable enable
namespace SmartProperty.Api.Contracts.Authentication;

public sealed record AuthContextResponse(
    AuthContextUserResponse User,
    IReadOnlyList<string> PlatformRoles,
    IReadOnlyList<string> PlatformPermissions,
    IReadOnlyList<AuthContextWorkspaceResponse> Workspaces);

public sealed record AuthContextUserResponse(
    Guid Id,
    string Email,
    string FirstName,
    string LastName);

public sealed record AuthContextWorkspaceResponse(
    Guid Id,
    string Name,
    IReadOnlyList<string> Roles,
    IReadOnlyList<string> Permissions);
