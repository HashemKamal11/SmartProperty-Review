using SmartProperty.Application.Abstractions.Persistence.Repositories;
using SmartProperty.Application.Authentication.Me;
using SmartProperty.Common.Results;
using SmartProperty.Domain.Identity;

namespace SmartProperty.Application.Marketplace.ListingSubmissions.Write;

/// <summary>
/// Confirms that the account behind an authenticated user is still in good standing, from the persisted user.
/// </summary>
/// <remarks>
/// A valid token proves who the caller was, not their standing now, so every write reads the account itself. The
/// answers are the established ones: an account that no longer exists is the same <c>401</c> as no token, so an id is
/// never confirmed real, and every non-active status is the one account-level <c>403</c>, so the exact status is not
/// disclosed. A failed lookup propagates; it is never an answer. Nothing is read from a token or a claim.
/// </remarks>
internal static class ActiveAccountGate
{
    /// <returns>Null when the account is Active; otherwise the error to return before anything else is read.</returns>
    public static async Task<Error?> CheckAsync(
        IUserRepository users,
        Guid userId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(users);

        var account = await users.GetByIdAsync(userId, cancellationToken);

        if (account is null)
        {
            return ListingSubmissionErrors.Unauthorized;
        }

        return account.Status == UserStatus.Active ? null : MeErrors.AccountUnavailable;
    }
}
