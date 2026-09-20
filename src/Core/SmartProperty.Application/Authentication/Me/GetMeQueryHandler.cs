using SmartProperty.Application.Abstractions.Identity;
using SmartProperty.Application.Abstractions.Messaging;
using SmartProperty.Application.Abstractions.Persistence.Repositories;
using SmartProperty.Common.Results;
using SmartProperty.Domain.Identity;

namespace SmartProperty.Application.Authentication.Me;

/// <summary>
/// Returns the authenticated user's current profile. Read-only: it issues no tokens, touches no credential or
/// refresh token, and never saves. The account status is re-read from the database, so an access token issued
/// while the user was active stops working here once the account is no longer active.
/// </summary>
public sealed class GetMeQueryHandler(
    ICurrentUser currentUser,
    IUserRepository userRepository)
    : IQueryHandler<GetMeQuery, MeResult>
{
    public async Task<Result<MeResult>> Handle(
        GetMeQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        // The endpoint requires authentication, so this is normally already satisfied. It is still checked
        // rather than assumed: an unusable subject fails closed instead of throwing.
        if (currentUser.UserId is not { } userId)
        {
            return Result<MeResult>.Failure(MeErrors.Unauthorized);
        }

        var user = await userRepository.GetByIdAsync(userId, cancellationToken);

        // A token can outlive the user it names. That is invalid session state, not a missing resource, so it
        // returns the same 401 as an unauthenticated request rather than revealing that the id is unknown.
        if (user is null)
        {
            return Result<MeResult>.Failure(MeErrors.Unauthorized);
        }

        if (user.Status != UserStatus.Active)
        {
            return Result<MeResult>.Failure(MeErrors.AccountUnavailable);
        }

        // Profile values come from the current row, never from the token: access tokens stay identity-only.
        return Result<MeResult>.Success(new MeResult(
            user.Id,
            user.Email,
            user.FirstName,
            user.LastName));
    }
}
