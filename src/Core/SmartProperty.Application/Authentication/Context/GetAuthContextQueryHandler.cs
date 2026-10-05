using SmartProperty.Application.Abstractions.Authorization;
using SmartProperty.Application.Abstractions.Identity;
using SmartProperty.Application.Abstractions.Messaging;
using SmartProperty.Application.Abstractions.Persistence.Repositories;
using SmartProperty.Application.Authentication.Me;
using SmartProperty.Common.Results;
using SmartProperty.Domain.Identity;

namespace SmartProperty.Application.Authentication.Context;

/// <summary>
/// Returns the authenticated active user's profile together with persisted platform and workspace access.
/// </summary>
public sealed class GetAuthContextQueryHandler(
    ICurrentUser currentUser,
    IUserRepository userRepository,
    IAccessContextReader accessContextReader)
    : IQueryHandler<GetAuthContextQuery, AuthContextResult>
{
    public async Task<Result<AuthContextResult>> Handle(
        GetAuthContextQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        if (currentUser.UserId is not { } userId)
        {
            return Result<AuthContextResult>.Failure(MeErrors.Unauthorized);
        }

        var user = await userRepository.GetByIdAsync(userId, cancellationToken);

        if (user is null)
        {
            return Result<AuthContextResult>.Failure(MeErrors.Unauthorized);
        }

        if (user.Status != UserStatus.Active)
        {
            return Result<AuthContextResult>.Failure(MeErrors.AccountUnavailable);
        }

        var access = await accessContextReader.ReadAsync(user.Id, cancellationToken);

        return Result<AuthContextResult>.Success(new AuthContextResult(
            new AuthContextUser(user.Id, user.Email, user.FirstName, user.LastName),
            access.PlatformRoles,
            access.PlatformPermissions,
            access.Workspaces
                .Select(workspace => new WorkspaceAuthContext(
                    workspace.Id,
                    workspace.Name,
                    workspace.Roles,
                    workspace.Permissions))
                .ToArray()));
    }
}
