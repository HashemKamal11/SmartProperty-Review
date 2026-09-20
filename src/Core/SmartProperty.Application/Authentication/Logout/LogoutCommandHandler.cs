using SmartProperty.Application.Abstractions.Authentication;
using SmartProperty.Application.Abstractions.Messaging;
using SmartProperty.Application.Abstractions.Persistence;
using SmartProperty.Application.Abstractions.Persistence.Repositories;
using SmartProperty.Application.Abstractions.Time;
using SmartProperty.Common.Results;

namespace SmartProperty.Application.Authentication.Logout;

/// <summary>
/// Revokes the refresh token presented by the caller, and nothing else: no replacement token is issued, and the
/// user's other refresh tokens are left alone. The operation is idempotent — an unknown, expired, already
/// revoked, or concurrently revoked token succeeds without changing anything — so a client can safely retry and
/// learns nothing about the token's state from the response.
/// </summary>
public sealed class LogoutCommandHandler(
    IRefreshTokenRepository refreshTokenRepository,
    ITokenProvider tokenProvider,
    IDateTimeProvider dateTimeProvider,
    IUnitOfWork unitOfWork)
    : ICommandHandler<LogoutCommand>
{
    public async Task<Result> Handle(
        LogoutCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (LogoutCommandValidator.Validate(command) is { } validationError)
        {
            return Result.Failure(validationError);
        }

        // Only the hash reaches the database; the raw token is never queried, stored, or logged.
        var tokenHash = tokenProvider.HashRefreshToken(command.RefreshToken);

        var refreshToken = await refreshTokenRepository.GetByTokenHashAsync(tokenHash, cancellationToken);

        var loggedOutAt = dateTimeProvider.UtcNow;

        // No user is loaded and no account status is checked: destroying a credential must stay possible for an
        // account that can no longer sign in, and a lookup would only add account-state exposure and work.

        // Nothing to revoke: the token is unknown, already revoked, or expired. RefreshToken.IsActive owns that
        // rule. Succeeding here rather than failing is what makes logout idempotent, and it keeps the response
        // from revealing whether the token ever existed.
        if (refreshToken is null || !refreshToken.IsActive(loggedOutAt))
        {
            return Result.Success();
        }

        refreshToken.Revoke(loggedOutAt);

        try
        {
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }
        catch (ConcurrencyConflictException exception)
            when (exception.Resource == PersistenceResource.RefreshToken)
        {
            // Another writer revoked or rotated this token first, so the save was rejected as a whole. The
            // presented credential is gone either way, which is exactly what logout set out to achieve, so the
            // race is reported as success rather than as a conflict.
            return Result.Success();
        }

        return Result.Success();
    }
}
