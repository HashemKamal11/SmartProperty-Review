using SmartProperty.Application.Abstractions.Messaging;
using SmartProperty.Application.Abstractions.Persistence;
using SmartProperty.Application.Abstractions.Persistence.Repositories;
using SmartProperty.Application.Abstractions.Time;
using SmartProperty.Application.Authorization;
using SmartProperty.Common.Results;
using SmartProperty.Domain.Identity;

namespace SmartProperty.Application.Identity.Bootstrap;

/// <summary>
/// Makes one existing account a Platform Administrator, creating the platform role and its currently declared
/// platform permissions if they are not there yet.
/// </summary>
/// <remarks>
/// This exists because platform authorization has a cold-start problem: only a user holding
/// <see cref="PermissionCodes.WorkspaceAccessRequestsReview"/> may review a workspace access request, and no
/// endpoint can grant that permission to the very first administrator. Bootstrap is the deliberate, narrow,
/// operator-invoked answer, and it is the only thing in the system that writes a platform role assignment.
///
/// What it cannot do matters as much as what it does:
/// <list type="bullet">
/// <item>it never creates a <see cref="User"/>, a <see cref="UserCredential"/>, or a password</item>
/// <item>it never names an account in code — the email is configuration, supplied at deployment time</item>
/// <item>it never revives a Suspended or Deactivated account</item>
/// <item>it grants only permissions explicitly declared for implemented endpoints</item>
/// </list>
///
/// Every write is guarded by a lookup, so running it a second time changes nothing: no duplicate permission,
/// role, role-permission, or platform-role row. All writes commit through one
/// <see cref="IUnitOfWork.SaveChangesAsync"/> call, so a half-provisioned administrator cannot be left behind.
///
/// The role is found by name, which is the only place a role name is used at all. It is a provisioning key, never
/// an authorization decision: nothing anywhere branches on the name to allow anything, as
/// docs/authorization-model.md requires.
/// </remarks>
public sealed class BootstrapPlatformAdminCommandHandler(
    IUserRepository userRepository,
    IPermissionRepository permissionRepository,
    IRoleRepository roleRepository,
    IDateTimeProvider dateTimeProvider,
    IUnitOfWork unitOfWork,
    IPlatformAdminBootstrapCoordinator bootstrapCoordinator)
    : ICommandHandler<BootstrapPlatformAdminCommand, BootstrapPlatformAdminResult>
{
    /// <summary>
    /// The platform role bootstrap provisions. A display name and a provisioning key only — see the remarks on
    /// this class for why nothing authorizes on it.
    /// </summary>
    public const string PlatformAdminRoleName = "Platform Admin";

    public async Task<Result<BootstrapPlatformAdminResult>> Handle(
        BootstrapPlatformAdminCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (string.IsNullOrWhiteSpace(command.PlatformAdminEmail))
        {
            return Result<BootstrapPlatformAdminResult>.Failure(
                BootstrapPlatformAdminErrors.PlatformAdminEmailMissing);
        }

        return await bootstrapCoordinator.ExecuteAsync(
            operationCancellationToken => BootstrapAsync(command, operationCancellationToken),
            cancellationToken);
    }

    private async Task<Result<BootstrapPlatformAdminResult>> BootstrapAsync(
        BootstrapPlatformAdminCommand command,
        CancellationToken cancellationToken)
    {
        // Every persistence read occurs only after the database-wide coordinator has been acquired. A waiter
        // therefore observes the first instance's committed rows instead of acting on a pre-lock snapshot.

        // GetByEmailAsync applies the same trim-and-lowercase normalization User's constructor does, so the
        // configured email matches the stored one however the operator cased or padded it.
        var user = await userRepository.GetByEmailAsync(command.PlatformAdminEmail, cancellationToken);

        if (user is null)
        {
            return Result<BootstrapPlatformAdminResult>.Failure(
                BootstrapPlatformAdminErrors.PlatformAdminNotFound);
        }

        if (user.Status is not (UserStatus.Pending or UserStatus.Active))
        {
            return Result<BootstrapPlatformAdminResult>.Failure(
                BootstrapPlatformAdminErrors.PlatformAdminAccountUnavailable);
        }

        var provisionedAt = dateTimeProvider.UtcNow;

        // Only Pending is promoted. An account that is already Active is left untouched, including its
        // UpdatedAt, so a repeated run is genuinely inert.
        var userActivated = user.Status == UserStatus.Pending;

        if (userActivated)
        {
            user.Activate(provisionedAt);
        }

        var permission = await permissionRepository.GetByCodeAsync(
            PermissionCodes.WorkspaceAccessRequestsReview,
            cancellationToken);

        var permissionCreated = permission is null;

        if (permission is null)
        {
            permission = new Permission(
                Guid.NewGuid(),
                PermissionCodes.WorkspaceAccessRequestsReview,
                PermissionCodes.WorkspaceAccessRequestsReviewDescription);

            await permissionRepository.AddAsync(permission, cancellationToken);
        }

        var propertyCreatePermission = await permissionRepository.GetByCodeAsync(
            PermissionCodes.PropertyCreate,
            cancellationToken);

        var propertyCreatePermissionCreated = propertyCreatePermission is null;

        if (propertyCreatePermission is null)
        {
            propertyCreatePermission = new Permission(
                Guid.NewGuid(),
                PermissionCodes.PropertyCreate,
                PermissionCodes.PropertyCreateDescription);

            await permissionRepository.AddAsync(propertyCreatePermission, cancellationToken);
        }

        var role = await roleRepository.GetPlatformRoleByNameAsync(PlatformAdminRoleName, cancellationToken);

        var roleCreated = role is null;

        if (role is null)
        {
            // Platform scope with a null workspace id: Role's constructor rejects any other pairing.
            role = new Role(
                Guid.NewGuid(),
                PlatformAdminRoleName,
                RoleScope.Platform,
                workspaceId: null,
                provisionedAt);

            await roleRepository.AddAsync(role, cancellationToken);
        }

        // The two checks below query by id, so a row created moments ago in this same unit of work is invisible
        // to them. That cannot duplicate anything: at most one of each row is added per run.
        var rolePermissionCreated = !await roleRepository.HasPermissionAsync(
            role.Id,
            permission.Id,
            cancellationToken);

        if (rolePermissionCreated)
        {
            await roleRepository.AddPermissionAsync(new RolePermission(role.Id, permission.Id), cancellationToken);
        }

        var propertyCreateRolePermissionCreated = !await roleRepository.HasPermissionAsync(
            role.Id,
            propertyCreatePermission.Id,
            cancellationToken);

        if (propertyCreateRolePermissionCreated)
        {
            await roleRepository.AddPermissionAsync(
                new RolePermission(role.Id, propertyCreatePermission.Id),
                cancellationToken);
        }

        var platformRoleAssigned = !await roleRepository.IsAssignedToUserAsync(
            user.Id,
            role.Id,
            cancellationToken);

        if (platformRoleAssigned)
        {
            await roleRepository.AddUserAssignmentAsync(
                new UserPlatformRole(user.Id, role.Id),
                cancellationToken);
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);

        return Result<BootstrapPlatformAdminResult>.Success(new BootstrapPlatformAdminResult(
            user.Id,
            user.Status,
            userActivated,
            permission.Id,
            permissionCreated,
            propertyCreatePermission.Id,
            propertyCreatePermissionCreated,
            role.Id,
            roleCreated,
            rolePermissionCreated,
            propertyCreateRolePermissionCreated,
            platformRoleAssigned));
    }
}
