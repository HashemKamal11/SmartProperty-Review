using SmartProperty.Application.Abstractions.Authorization;
using SmartProperty.Application.Authorization;

namespace SmartProperty.UnitTests.TestDoubles;

/// <summary>Allows exactly the (user, permission) pairs that were granted; everything else is denied.</summary>
internal sealed class FakePermissionChecker : IPermissionChecker
{
    private readonly HashSet<(Guid UserId, string PermissionCode)> _granted = [];

    public void Grant(Guid userId, string permissionCode) => _granted.Add((userId, permissionCode));

    public Task<AuthorizationDecision> CheckAsync(AuthorizationRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        return Task.FromResult(_granted.Contains((request.UserId, request.PermissionCode))
            ? AuthorizationDecision.Allowed
            : AuthorizationDecision.Denied);
    }
}
