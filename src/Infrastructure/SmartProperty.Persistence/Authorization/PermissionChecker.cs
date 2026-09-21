using Microsoft.EntityFrameworkCore;
using SmartProperty.Application.Abstractions.Authorization;
using SmartProperty.Application.Authorization;
using SmartProperty.Domain.Identity;
using SmartProperty.Persistence.Context;

namespace SmartProperty.Persistence.Authorization;

/// <summary>
/// Resolves permissions against current persisted access state.
/// </summary>
/// <remarks>
/// Each question is one existence query: the joins below mirror the paths in docs/authorization-model.md and end
/// in <see cref="EntityFrameworkQueryableExtensions.AnyAsync{TSource}(IQueryable{TSource},CancellationToken)"/>,
/// so the database answers yes or no and no part of the access graph is materialized.
///
/// Every predicate is restated in the query rather than assumed from how the domain constructors build data:
/// authorization has to fail closed even against rows that arrived by import or by hand, so an inconsistent
/// association — a membership in one workspace carrying a role that belongs to another — cannot satisfy a check.
///
/// Only known-negative answers become <see cref="AuthorizationDecision.Denied"/>. A persistence failure is not an
/// answer and is left to propagate; so is cancellation.
/// </remarks>
internal sealed class PermissionChecker(ApplicationDbContext dbContext) : IPermissionChecker
{
    public async Task<AuthorizationDecision> CheckAsync(
        AuthorizationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        // AuthorizationTarget cannot be constructed in any other shape, but an unrecognized one still denies:
        // there is no path through this method that allows without a query having said yes.
        var isAllowed = request.Target switch
        {
            { Scope: RoleScope.Platform, WorkspaceId: null } => await HasPlatformPermissionAsync(
                request.UserId,
                request.PermissionCode,
                cancellationToken),
            { Scope: RoleScope.Workspace, WorkspaceId: { } workspaceId } => await HasWorkspacePermissionAsync(
                request.UserId,
                request.PermissionCode,
                workspaceId,
                cancellationToken),
            _ => false
        };

        return isAllowed ? AuthorizationDecision.Allowed : AuthorizationDecision.Denied;
    }

    /// <summary>
    /// User -> UserPlatformRole -> Role (Platform) -> RolePermission -> Permission.
    /// </summary>
    /// <remarks>
    /// Workspace memberships and workspace roles are absent from this query by design: nothing held inside a
    /// workspace can satisfy a platform target.
    /// </remarks>
    private Task<bool> HasPlatformPermissionAsync(
        Guid userId,
        string permissionCode,
        CancellationToken cancellationToken)
    {
        var grants =
            from user in dbContext.Users
            join assignment in dbContext.UserPlatformRoles on user.Id equals assignment.UserId
            join role in dbContext.Roles on assignment.RoleId equals role.Id
            join rolePermission in dbContext.RolePermissions on role.Id equals rolePermission.RoleId
            join permission in dbContext.Permissions on rolePermission.PermissionId equals permission.Id
            where user.Id == userId
                && user.Status == UserStatus.Active
                && role.Scope == RoleScope.Platform
                && role.WorkspaceId == null
                && permission.Code == permissionCode
            select 1;

        return grants.AnyAsync(cancellationToken);
    }

    /// <summary>
    /// User -> WorkspaceMembership -> WorkspaceMembershipRole -> Role (Workspace) -> RolePermission -> Permission.
    /// </summary>
    /// <remarks>
    /// The target workspace is constrained twice — on the membership and on the role — so a grant in workspace A
    /// can never answer a question about workspace B, whichever of the two rows is the inconsistent one.
    /// </remarks>
    private Task<bool> HasWorkspacePermissionAsync(
        Guid userId,
        string permissionCode,
        Guid workspaceId,
        CancellationToken cancellationToken)
    {
        var grants =
            from user in dbContext.Users
            join membership in dbContext.WorkspaceMemberships on user.Id equals membership.UserId
            join membershipRole in dbContext.WorkspaceMembershipRoles
                on membership.Id equals membershipRole.WorkspaceMembershipId
            join role in dbContext.Roles on membershipRole.RoleId equals role.Id
            join rolePermission in dbContext.RolePermissions on role.Id equals rolePermission.RoleId
            join permission in dbContext.Permissions on rolePermission.PermissionId equals permission.Id
            where user.Id == userId
                && user.Status == UserStatus.Active
                && membership.WorkspaceId == workspaceId
                && role.Scope == RoleScope.Workspace
                && role.WorkspaceId == workspaceId
                && permission.Code == permissionCode
            select 1;

        return grants.AnyAsync(cancellationToken);
    }
}
