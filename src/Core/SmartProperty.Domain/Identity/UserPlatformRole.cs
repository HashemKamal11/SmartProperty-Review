namespace SmartProperty.Domain.Identity;

/// <summary>
/// Assigns a platform-scoped role to a user. Application workflows must ensure the role scope is Platform.
/// </summary>
public sealed class UserPlatformRole
{
    private UserPlatformRole()
    {
    }

    public UserPlatformRole(Guid userId, Guid roleId)
    {
        ValidateRequiredId(userId, nameof(userId));
        ValidateRequiredId(roleId, nameof(roleId));

        UserId = userId;
        RoleId = roleId;
    }

    public Guid UserId { get; private set; }
    public Guid RoleId { get; private set; }

    private static void ValidateRequiredId(Guid id, string parameterName)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException("Id must not be empty.", parameterName);
        }
    }
}
