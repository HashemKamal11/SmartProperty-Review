using SmartProperty.Application.Abstractions.Authorization;
using SmartProperty.Application.Abstractions.Persistence.Repositories;
using SmartProperty.Application.Authorization;

namespace SmartProperty.Application.PropertyRegistry;

/// <summary>
/// Who may read which registry properties. A platform administrator (holding <c>property.update</c>) reads every
/// property; anyone else signed in reads only the properties they registered. Anonymous visitors read none: the public
/// sees a property only through its published marketplace listing.
/// </summary>
public sealed class PropertyReadAccess(IPropertyRepository propertyRepository, IPermissionChecker permissionChecker)
{
    /// <summary>What a reader is told about a property they may not read: the same 404 as one that does not exist.</summary>
    public static Common.Results.Error NotFound => GetById.GetPropertyByIdErrors.NotFound;

    /// <summary>The owner to restrict a list to, or <see langword="null"/> when the reader may see every property.</summary>
    public async Task<Guid?> OwnerScopeAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfEqual(userId, Guid.Empty);

        return await IsAdministratorAsync(userId, cancellationToken) ? null : userId;
    }

    /// <summary>True when the reader may see this property: an administrator, or the user who registered it.</summary>
    public async Task<bool> CanReadAsync(Guid userId, Guid propertyId, CancellationToken cancellationToken = default)
    {
        if (userId == Guid.Empty || propertyId == Guid.Empty)
        {
            return false;
        }

        if (await IsAdministratorAsync(userId, cancellationToken))
        {
            return true;
        }

        var property = await propertyRepository.GetByIdAsync(propertyId, cancellationToken);

        return property is not null && property.OwnerUserId == userId;
    }

    private async Task<bool> IsAdministratorAsync(Guid userId, CancellationToken cancellationToken)
    {
        var decision = await permissionChecker.CheckAsync(
            AuthorizationRequest.For(userId, PermissionCodes.PropertyUpdate, AuthorizationTarget.Platform),
            cancellationToken);

        return decision.IsAllowed;
    }
}
