namespace SmartProperty.Application.Authorization;

/// <summary>
/// One authorization question: may this user exercise this permission against this target?
/// </summary>
/// <remarks>
/// The user is identified by id rather than by a principal or an HTTP context, so authorization stays
/// framework-neutral and testable. Resolving the current request's user is the API's job.
/// </remarks>
public sealed record AuthorizationRequest
{
    private AuthorizationRequest(Guid userId, string permissionCode, AuthorizationTarget target)
    {
        UserId = userId;
        PermissionCode = permissionCode;
        Target = target;
    }

    /// <exception cref="ArgumentException">The user id is empty, or the permission code is blank.</exception>
    public static AuthorizationRequest For(Guid userId, string permissionCode, AuthorizationTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);

        if (userId == Guid.Empty)
        {
            throw new ArgumentException("User id must not be empty.", nameof(userId));
        }

        if (string.IsNullOrWhiteSpace(permissionCode))
        {
            throw new ArgumentException("Permission code must not be empty.", nameof(permissionCode));
        }

        // Trimmed to match how Permission stores its code and how the permission repository looks one up.
        // Case is left alone: permission codes are compared exactly (see docs/authorization-model.md).
        return new AuthorizationRequest(userId, permissionCode.Trim(), target);
    }

    public Guid UserId { get; }

    /// <summary>The stable machine-readable <c>Permission.Code</c>, never a role or display name.</summary>
    public string PermissionCode { get; }

    public AuthorizationTarget Target { get; }
}
