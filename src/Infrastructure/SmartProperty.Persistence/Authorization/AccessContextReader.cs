using Microsoft.EntityFrameworkCore;
using SmartProperty.Application.Abstractions.Authorization;
using SmartProperty.Domain.Identity;
using SmartProperty.Persistence.Context;

namespace SmartProperty.Persistence.Authorization;

/// <summary>
/// Projects the same persisted access paths enforced by <see cref="PermissionChecker"/> into an auth-context
/// snapshot. The caller has already established that the user is Active; the predicates repeat that condition so
/// this reader cannot expose grants that the checker would deny if it is called independently.
/// </summary>
internal sealed class AccessContextReader(ApplicationDbContext dbContext) : IAccessContextReader
{
    public async Task<AccessContext> ReadAsync(
        Guid userId,
        CancellationToken cancellationToken = default)
    {
        if (userId == Guid.Empty)
        {
            throw new ArgumentException("User id must not be empty.", nameof(userId));
        }

        var platformRows = await ReadPlatformAsync(userId, cancellationToken);
        var workspaceRows = await ReadWorkspacesAsync(userId, cancellationToken);

        var platformRoles = DistinctOrdinal(platformRows.Select(row => row.RoleName));
        var platformPermissions = DistinctOrdinal(
            platformRows
                .Where(row => row.PermissionCode is not null)
                .Select(row => row.PermissionCode!));

        var workspaces = workspaceRows
            .GroupBy(row => new { row.WorkspaceId, row.WorkspaceName })
            .Select(group => new WorkspaceAccessContext(
                group.Key.WorkspaceId,
                group.Key.WorkspaceName,
                DistinctOrdinal(
                    group.Where(row => row.RoleName is not null).Select(row => row.RoleName!)),
                DistinctOrdinal(
                    group.Where(row => row.PermissionCode is not null).Select(row => row.PermissionCode!))))
            .OrderBy(workspace => workspace.Name, StringComparer.Ordinal)
            .ThenBy(workspace => workspace.Id)
            .ToArray();

        return new AccessContext(platformRoles, platformPermissions, workspaces);
    }

    private async Task<PlatformAccessRow[]> ReadPlatformAsync(
        Guid userId,
        CancellationToken cancellationToken)
    {
        return await (
            from user in dbContext.Users.AsNoTracking()
            join assignment in dbContext.UserPlatformRoles.AsNoTracking() on user.Id equals assignment.UserId
            join role in dbContext.Roles.AsNoTracking() on assignment.RoleId equals role.Id
            join rolePermission in dbContext.RolePermissions.AsNoTracking()
                on role.Id equals rolePermission.RoleId into rolePermissions
            from rolePermission in rolePermissions.DefaultIfEmpty()
            join permission in dbContext.Permissions.AsNoTracking()
                on rolePermission.PermissionId equals permission.Id into permissions
            from permission in permissions.DefaultIfEmpty()
            where user.Id == userId
                && user.Status == UserStatus.Active
                && role.Scope == RoleScope.Platform
                && role.WorkspaceId == null
            select new PlatformAccessRow(
                role.Name,
                permission == null ? null : permission.Code))
            .ToArrayAsync(cancellationToken);
    }

    private async Task<WorkspaceAccessRow[]> ReadWorkspacesAsync(
        Guid userId,
        CancellationToken cancellationToken)
    {
        return await (
            from user in dbContext.Users.AsNoTracking()
            join membership in dbContext.WorkspaceMemberships.AsNoTracking() on user.Id equals membership.UserId
            join workspace in dbContext.Workspaces.AsNoTracking() on membership.WorkspaceId equals workspace.Id
            join membershipRole in dbContext.WorkspaceMembershipRoles.AsNoTracking()
                on membership.Id equals membershipRole.WorkspaceMembershipId into membershipRoles
            from membershipRole in membershipRoles.DefaultIfEmpty()
            join candidateRole in dbContext.Roles.AsNoTracking()
                on membershipRole.RoleId equals candidateRole.Id into candidateRoles
            from candidateRole in candidateRoles.DefaultIfEmpty()
            join rolePermission in dbContext.RolePermissions.AsNoTracking()
                on candidateRole.Id equals rolePermission.RoleId into rolePermissions
            from rolePermission in rolePermissions.DefaultIfEmpty()
            join permission in dbContext.Permissions.AsNoTracking()
                on rolePermission.PermissionId equals permission.Id into permissions
            from permission in permissions.DefaultIfEmpty()
            let isValidRole = candidateRole != null
                && candidateRole.Scope == RoleScope.Workspace
                && candidateRole.WorkspaceId == membership.WorkspaceId
            where user.Id == userId && user.Status == UserStatus.Active
            select new WorkspaceAccessRow(
                workspace.Id,
                workspace.Name,
                isValidRole ? candidateRole!.Name : null,
                isValidRole && permission != null ? permission.Code : null))
            .ToArrayAsync(cancellationToken);
    }

    private static string[] DistinctOrdinal(IEnumerable<string> values)
    {
        return values
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
    }

    private sealed record PlatformAccessRow(string RoleName, string? PermissionCode);

    private sealed record WorkspaceAccessRow(
        Guid WorkspaceId,
        string WorkspaceName,
        string? RoleName,
        string? PermissionCode);
}
