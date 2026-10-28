using SmartProperty.Application.Abstractions.Identity;
using SmartProperty.Application.Abstractions.Persistence.Repositories;
using SmartProperty.Common.Results;

namespace SmartProperty.Application.Marketplace.ListingSubmissions.Write;

/// <summary>
/// Who is writing: the authenticated user, read from <see cref="ICurrentUser"/> alone, whose persisted account is
/// Active.
/// </summary>
/// <remarks>
/// Every write handler starts here. An unusable subject is a 401 before anything is read, and an account that is
/// missing or not Active is refused by <see cref="ActiveAccountGate"/>. No request value is ever consulted.
/// </remarks>
internal readonly record struct ActingWriter(Guid UserId, Error? Error)
{
    public static async Task<ActingWriter> ResolveAsync(
        ICurrentUser currentUser,
        IUserRepository users,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(currentUser);
        ArgumentNullException.ThrowIfNull(users);

        if (currentUser.UserId is not { } userId || userId == Guid.Empty)
        {
            return new ActingWriter(Guid.Empty, ListingSubmissionErrors.Unauthorized);
        }

        return new ActingWriter(userId, await ActiveAccountGate.CheckAsync(users, userId, cancellationToken));
    }
}
