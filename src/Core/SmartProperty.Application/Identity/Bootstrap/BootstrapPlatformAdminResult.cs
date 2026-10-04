using SmartProperty.Domain.Identity;

namespace SmartProperty.Application.Identity.Bootstrap;

/// <summary>
/// What the bootstrap actually changed. Every flag is false on a second run, which is how idempotency is
/// observed rather than assumed.
/// </summary>
public sealed record BootstrapPlatformAdminResult(
    Guid UserId,
    UserStatus UserStatus,
    bool UserActivated,
    Guid PermissionId,
    bool PermissionCreated,
    Guid PropertyCreatePermissionId,
    bool PropertyCreatePermissionCreated,
    Guid RoleId,
    bool RoleCreated,
    bool RolePermissionCreated,
    bool PropertyCreateRolePermissionCreated,
    bool PlatformRoleAssigned)
{
    /// <summary>True when the run left the database exactly as it found it.</summary>
    public bool MadeNoChange =>
        !UserActivated
        && !PermissionCreated
        && !PropertyCreatePermissionCreated
        && !RoleCreated
        && !RolePermissionCreated
        && !PropertyCreateRolePermissionCreated
        && !PlatformRoleAssigned;
}
