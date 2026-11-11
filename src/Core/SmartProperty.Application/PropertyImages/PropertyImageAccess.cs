using SmartProperty.Application.Abstractions.Authorization;
using SmartProperty.Application.Abstractions.Persistence.Repositories;
using SmartProperty.Application.Authorization;
using SmartProperty.Common.Results;
using SmartProperty.Domain.PropertyRegistry;

namespace SmartProperty.Application.PropertyImages;

/// <summary>
/// Who may change the photos of a property: anyone holding <see cref="PermissionCodes.PropertyUpdate"/> at platform
/// scope (administrators), or the property's own owner while they are still an approved owner.
/// </summary>
/// <remarks>
/// "Approved owner" is <see cref="PermissionCodes.PropertyCreate"/>, the permission owner approval grants and an edit
/// of the profile withdraws, so an owner whose approval was withdrawn can no longer change their photos either.
/// Ownership never grants anything beyond the photos: owners still cannot update or archive the property itself.
/// The order is not found, then forbidden, then archived; a property's existence is public anyway (reads are public).
/// </remarks>
public sealed class PropertyImageAccess(IPropertyRepository propertyRepository, IPermissionChecker permissionChecker)
{
    public static readonly Error Forbidden = new(
        "authorization.forbidden",
        "You do not have permission to access this resource.",
        ErrorType.Forbidden);

    /// <summary>Success when <paramref name="userId"/> may change the photos of the active property.</summary>
    public async Task<Result> EnsureCanManageAsync(Guid userId, Guid propertyId, CancellationToken cancellationToken)
    {
        if (propertyId == Guid.Empty)
        {
            return Result.Failure(PropertyImageErrors.PropertyNotFound);
        }

        var property = await propertyRepository.GetByIdAsync(propertyId, cancellationToken);

        if (property is null)
        {
            return Result.Failure(PropertyImageErrors.PropertyNotFound);
        }

        if (!await CanManageAsync(userId, property, cancellationToken))
        {
            return Result.Failure(Forbidden);
        }

        return property.Status == PropertyStatus.Active
            ? Result.Success()
            : Result.Failure(PropertyImageErrors.PropertyArchived);
    }

    private async Task<bool> CanManageAsync(Guid userId, Property property, CancellationToken cancellationToken)
    {
        if (userId == Guid.Empty)
        {
            return false;
        }

        if (await HasAsync(userId, PermissionCodes.PropertyUpdate, cancellationToken))
        {
            return true;
        }

        return property.OwnerUserId == userId
            && await HasAsync(userId, PermissionCodes.PropertyCreate, cancellationToken);
    }

    private async Task<bool> HasAsync(Guid userId, string permissionCode, CancellationToken cancellationToken)
    {
        var decision = await permissionChecker.CheckAsync(
            AuthorizationRequest.For(userId, permissionCode, AuthorizationTarget.Platform),
            cancellationToken);

        return decision.IsAllowed;
    }
}
