using SmartProperty.Domain.Identity;

namespace SmartProperty.Application.Authorization;

/// <summary>
/// What an authorization question is being asked about: the platform as a whole, or one workspace.
/// </summary>
/// <remarks>
/// Instances are created only through <see cref="Platform"/> and <see cref="Workspace"/>, so the combinations
/// that would be meaningless — a platform target carrying a workspace id, or a workspace target without one —
/// cannot be constructed. Callers therefore never have to re-validate the pairing.
///
/// <see cref="RoleScope"/> is reused rather than duplicated: the scope a role must have to satisfy a target is
/// exactly the target's own scope, so a second Platform/Workspace enum would only be able to disagree with it.
/// </remarks>
public sealed record AuthorizationTarget
{
    private AuthorizationTarget(RoleScope scope, Guid? workspaceId)
    {
        Scope = scope;
        WorkspaceId = workspaceId;
    }

    /// <summary>The platform as a whole. Satisfied only by platform-scoped roles.</summary>
    public static AuthorizationTarget Platform { get; } = new(RoleScope.Platform, workspaceId: null);

    /// <summary>One workspace. Satisfied only by workspace-scoped roles held in that same workspace.</summary>
    /// <exception cref="ArgumentException">The workspace id is empty.</exception>
    public static AuthorizationTarget Workspace(Guid workspaceId)
    {
        if (workspaceId == Guid.Empty)
        {
            throw new ArgumentException("Workspace id must not be empty.", nameof(workspaceId));
        }

        return new AuthorizationTarget(RoleScope.Workspace, workspaceId);
    }

    /// <summary>The role scope that can satisfy this target.</summary>
    public RoleScope Scope { get; }

    /// <summary>The workspace, for a workspace target; null for the platform target.</summary>
    public Guid? WorkspaceId { get; }

    public override string ToString()
    {
        return WorkspaceId is { } workspaceId
            ? $"{nameof(AuthorizationTarget)} {{ {Scope}, {workspaceId} }}"
            : $"{nameof(AuthorizationTarget)} {{ {Scope} }}";
    }
}
