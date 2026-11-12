using SmartProperty.Application.Abstractions.Persistence;
using SmartProperty.Application.Abstractions.Persistence.Repositories;
using SmartProperty.Application.Abstractions.Time;
using SmartProperty.Application.Authorization;
using SmartProperty.Application.Identity.Bootstrap;
using SmartProperty.Domain.Identity;

namespace SmartProperty.Application.Identity.StaffRoles;

/// <summary>What the catalogue changed on one run.</summary>
public sealed record StaffRoleCatalogResult(int PermissionsCreated, bool LegalReviewerRoleCreated, int RolePermissionsAttached)
{
    public bool MadeNoChange => PermissionsCreated == 0 && !LegalReviewerRoleCreated && RolePermissionsAttached == 0;
}

/// <summary>
/// The platform roles of the advertising workflow, provisioned idempotently at every start so the real flow works
/// without any Development tool or hand-written grant.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><b>Platform Admin</b> (created by the bootstrap; left alone when it does not exist yet) additionally holds the
/// administrator's side: issue, read and revoke authority grants, manage the marketplace settings, and manage staff
/// (assign legal reviewers). It deliberately does <b>not</b> hold the legal review permission.</item>
/// <item><b>Legal Reviewer</b> holds exactly the legal review permission and the read permission. It is assigned to
/// people by a platform administrator through the staff API.</item>
/// </list>
/// <para>
/// Runs under the same database-wide lock as the platform administrator bootstrap, so concurrent starts never create
/// duplicates. Only adds; never removes a permission or an assignment.
/// </para>
/// </remarks>
public sealed class MarketplaceStaffRoleCatalog(
    IPermissionRepository permissions,
    IRoleRepository roles,
    IDateTimeProvider clock,
    IUnitOfWork unitOfWork,
    IPlatformAdminBootstrapCoordinator coordinator)
{
    public const string LegalReviewerRoleName = "Legal Reviewer";

    public static IReadOnlyList<string> AdministratorPermissions { get; } =
    [
        PermissionCodes.MarketplaceAuthorityGrantsIssue,
        PermissionCodes.MarketplaceAuthorityGrantsRead,
        PermissionCodes.MarketplaceAuthorityGrantsRevoke,
        PermissionCodes.MarketplaceSettingsManage,
        PermissionCodes.PlatformStaffManage
    ];

    public static IReadOnlyList<string> LegalReviewerPermissions { get; } =
    [
        PermissionCodes.MarketplaceAuthorityLegalReviewsRecord,
        PermissionCodes.MarketplaceAuthorityGrantsRead
    ];

    private static readonly IReadOnlyDictionary<string, string> Descriptions = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        [PermissionCodes.MarketplaceAuthorityGrantsIssue] = PermissionCodes.MarketplaceAuthorityGrantsIssueDescription,
        [PermissionCodes.MarketplaceAuthorityGrantsRead] = PermissionCodes.MarketplaceAuthorityGrantsReadDescription,
        [PermissionCodes.MarketplaceAuthorityGrantsRevoke] = PermissionCodes.MarketplaceAuthorityGrantsRevokeDescription,
        [PermissionCodes.MarketplaceSettingsManage] = PermissionCodes.MarketplaceSettingsManageDescription,
        [PermissionCodes.PlatformStaffManage] = PermissionCodes.PlatformStaffManageDescription,
        [PermissionCodes.MarketplaceAuthorityLegalReviewsRecord] = PermissionCodes.MarketplaceAuthorityLegalReviewsRecordDescription
    };

    public Task<StaffRoleCatalogResult> EnsureAsync(CancellationToken cancellationToken = default) =>
        coordinator.ExecuteAsync(EnsureLockedAsync, cancellationToken);

    private async Task<StaffRoleCatalogResult> EnsureLockedAsync(CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;
        var created = 0;
        var attached = 0;
        var byCode = new Dictionary<string, Permission>(StringComparer.Ordinal);

        foreach (var (code, description) in Descriptions)
        {
            var permission = await permissions.GetByCodeAsync(code, cancellationToken);

            if (permission is null)
            {
                permission = new Permission(Guid.NewGuid(), code, description);
                await permissions.AddAsync(permission, cancellationToken);
                created++;
            }

            byCode[code] = permission;
        }

        var legalReviewer = await roles.GetPlatformRoleByNameAsync(LegalReviewerRoleName, cancellationToken);
        var legalReviewerCreated = legalReviewer is null;

        if (legalReviewer is null)
        {
            legalReviewer = new Role(Guid.NewGuid(), LegalReviewerRoleName, RoleScope.Platform, workspaceId: null, now);
            await roles.AddAsync(legalReviewer, cancellationToken);
        }

        attached += await AttachAsync(legalReviewer, LegalReviewerPermissions, byCode, legalReviewerCreated, cancellationToken);

        var administrator = await roles.GetPlatformRoleByNameAsync(BootstrapPlatformAdminCommandHandler.PlatformAdminRoleName, cancellationToken);

        if (administrator is not null)
        {
            attached += await AttachAsync(administrator, AdministratorPermissions, byCode, roleIsNew: false, cancellationToken);
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);

        return new StaffRoleCatalogResult(created, legalReviewerCreated, attached);
    }

    private async Task<int> AttachAsync(
        Role role, IReadOnlyList<string> codes, Dictionary<string, Permission> byCode, bool roleIsNew, CancellationToken cancellationToken)
    {
        var attached = 0;

        foreach (var code in codes)
        {
            var permission = byCode[code];

            // A role or permission created in this run cannot have the link yet; asking would miss unsaved rows anyway.
            if (roleIsNew || !await roles.HasPermissionAsync(role.Id, permission.Id, cancellationToken))
            {
                await roles.AddPermissionAsync(new RolePermission(role.Id, permission.Id), cancellationToken);
                attached++;
            }
        }

        return attached;
    }
}
