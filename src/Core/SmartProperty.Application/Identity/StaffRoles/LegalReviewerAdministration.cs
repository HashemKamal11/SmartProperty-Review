using SmartProperty.Application.Abstractions.Authorization;
using SmartProperty.Application.Abstractions.Identity;
using SmartProperty.Application.Abstractions.Persistence;
using SmartProperty.Application.Abstractions.Persistence.Repositories;
using SmartProperty.Application.Authorization;
using SmartProperty.Common.Results;
using SmartProperty.Domain.Identity;

namespace SmartProperty.Application.Identity.StaffRoles;

public sealed record StaffMemberView(Guid UserId, string Email, string FirstName, string LastName, string Status);

public static class StaffRoleErrors
{
    public static readonly Error Forbidden = new(
        "authorization.forbidden", "You do not have permission to access this resource.", ErrorType.Forbidden);

    public static readonly Error UserNotFound = new("staff.user_not_found", "No account has that email.", ErrorType.NotFound);

    public static readonly Error UserNotEligible = new(
        "staff.user_not_eligible",
        "Only an Active account with a verified email can be given a staff role.",
        ErrorType.Conflict);

    public static readonly Error CannotAssignSelf = new(
        "staff.cannot_assign_self", "You cannot change your own staff roles.", ErrorType.Conflict);

    public static readonly Error RoleMissing = new(
        "staff.role_missing", "The Legal Reviewer role has not been provisioned yet. Restart the API to provision it.", ErrorType.Conflict);

    public static readonly Error NotAssigned = new("staff.not_assigned", "That user is not a legal reviewer.", ErrorType.NotFound);

    public static Error Validation(string description) => new("validation.failed", description, ErrorType.Validation);
}

/// <summary>
/// A platform administrator gives or takes away the Legal Reviewer role. Needs <see cref="PermissionCodes.PlatformStaffManage"/>.
/// Nobody changes their own roles, and only an Active account with a verified email can become a reviewer.
/// </summary>
public sealed class LegalReviewerAdministration(
    ICurrentUser currentUser,
    IUserRepository users,
    IRoleRepository roles,
    IPermissionChecker permissions,
    IUnitOfWork unitOfWork)
{
    public async Task<Result<IReadOnlyList<StaffMemberView>>> ListAsync(CancellationToken cancellationToken = default)
    {
        if (await GateAsync(cancellationToken) is { } error)
        {
            return Result<IReadOnlyList<StaffMemberView>>.Failure(error);
        }

        var role = await roles.GetPlatformRoleByNameAsync(MarketplaceStaffRoleCatalog.LegalReviewerRoleName, cancellationToken);

        if (role is null)
        {
            return Result<IReadOnlyList<StaffMemberView>>.Success([]);
        }

        var members = new List<StaffMemberView>();

        foreach (var userId in await roles.ListAssignedUserIdsAsync(role.Id, cancellationToken))
        {
            if (await users.GetByIdAsync(userId, cancellationToken) is { } user)
            {
                members.Add(View(user));
            }
        }

        return Result<IReadOnlyList<StaffMemberView>>.Success(members.OrderBy(member => member.Email, StringComparer.Ordinal).ToArray());
    }

    public async Task<Result<StaffMemberView>> AssignAsync(string? email, CancellationToken cancellationToken = default)
    {
        if (await GateAsync(cancellationToken) is { } error)
        {
            return Result<StaffMemberView>.Failure(error);
        }

        if (string.IsNullOrWhiteSpace(email))
        {
            return Result<StaffMemberView>.Failure(StaffRoleErrors.Validation("email is required."));
        }

        var user = await users.GetByEmailAsync(email, cancellationToken);

        if (user is null)
        {
            return Result<StaffMemberView>.Failure(StaffRoleErrors.UserNotFound);
        }

        if (user.Id == currentUser.UserId)
        {
            return Result<StaffMemberView>.Failure(StaffRoleErrors.CannotAssignSelf);
        }

        if (user.Status != UserStatus.Active || user.EmailVerifiedAt is null)
        {
            return Result<StaffMemberView>.Failure(StaffRoleErrors.UserNotEligible);
        }

        var role = await roles.GetPlatformRoleByNameAsync(MarketplaceStaffRoleCatalog.LegalReviewerRoleName, cancellationToken);

        if (role is null)
        {
            return Result<StaffMemberView>.Failure(StaffRoleErrors.RoleMissing);
        }

        if (!await roles.IsAssignedToUserAsync(user.Id, role.Id, cancellationToken))
        {
            await roles.AddUserAssignmentAsync(new UserPlatformRole(user.Id, role.Id), cancellationToken);
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }

        return Result<StaffMemberView>.Success(View(user));
    }

    public async Task<Result<StaffMemberView>> RemoveAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        if (await GateAsync(cancellationToken) is { } error)
        {
            return Result<StaffMemberView>.Failure(error);
        }

        if (userId == currentUser.UserId)
        {
            return Result<StaffMemberView>.Failure(StaffRoleErrors.CannotAssignSelf);
        }

        var role = await roles.GetPlatformRoleByNameAsync(MarketplaceStaffRoleCatalog.LegalReviewerRoleName, cancellationToken);
        var user = userId == Guid.Empty ? null : await users.GetByIdAsync(userId, cancellationToken);

        if (role is null || user is null || !await roles.RemoveUserAssignmentAsync(userId, role.Id, cancellationToken))
        {
            return Result<StaffMemberView>.Failure(StaffRoleErrors.NotAssigned);
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);

        return Result<StaffMemberView>.Success(View(user));
    }

    private async Task<Error?> GateAsync(CancellationToken cancellationToken)
    {
        if (currentUser.UserId is not { } userId || userId == Guid.Empty)
        {
            return StaffRoleErrors.Forbidden;
        }

        var actor = await users.GetByIdAsync(userId, cancellationToken);

        if (actor is null || actor.Status != UserStatus.Active)
        {
            return StaffRoleErrors.Forbidden;
        }

        var decision = await permissions.CheckAsync(
            AuthorizationRequest.For(userId, PermissionCodes.PlatformStaffManage, AuthorizationTarget.Platform), cancellationToken);

        return decision.IsAllowed ? null : StaffRoleErrors.Forbidden;
    }

    private static StaffMemberView View(User user) =>
        new(user.Id, user.Email, user.FirstName, user.LastName, user.Status.ToString());
}
