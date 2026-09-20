using SmartProperty.Application.Abstractions.Authentication;
using SmartProperty.Application.Abstractions.Messaging;
using SmartProperty.Application.Abstractions.Persistence;
using SmartProperty.Application.Abstractions.Persistence.Repositories;
using SmartProperty.Application.Abstractions.Time;
using SmartProperty.Common.Results;
using SmartProperty.Domain.Identity;

namespace SmartProperty.Application.Authentication.Refresh;

/// <summary>
/// Rotates an active refresh token for an active user: revokes the presented token and issues a new access token
/// and a new refresh token in a single atomic save. A refresh token can rotate successfully only once.
/// </summary>
public sealed class RefreshCommandHandler(
    IRefreshTokenRepository refreshTokenRepository,
    IUserRepository userRepository,
    ITokenProvider tokenProvider,
    IDateTimeProvider dateTimeProvider,
    IUnitOfWork unitOfWork)
    : ICommandHandler<RefreshCommand, RefreshResult>
{
    public async Task<Result<RefreshResult>> Handle(
        RefreshCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (RefreshCommandValidator.Validate(command) is { } validationError)
        {
            return Result<RefreshResult>.Failure(validationError);
        }

        // Only the hash reaches the database; the raw token is never queried, stored, or logged.
        var tokenHash = tokenProvider.HashRefreshToken(command.RefreshToken);

        var refreshToken = await refreshTokenRepository.GetByTokenHashAsync(tokenHash, cancellationToken);

        if (refreshToken is null)
        {
            return Result<RefreshResult>.Failure(RefreshErrors.InvalidRefreshToken);
        }

        var refreshedAt = dateTimeProvider.UtcNow;

        // RefreshToken.IsActive owns the rule: not revoked, and now strictly before ExpiresAt. A token that has
        // already rotated is revoked, so replay is rejected here, before anything is mutated.
        if (!refreshToken.IsActive(refreshedAt))
        {
            return Result<RefreshResult>.Failure(RefreshErrors.InvalidRefreshToken);
        }

        var user = await userRepository.GetByIdAsync(refreshToken.UserId, cancellationToken);

        // A token without its user is invalid session state, not a distinguishable case for the caller.
        if (user is null)
        {
            return Result<RefreshResult>.Failure(RefreshErrors.InvalidRefreshToken);
        }

        // Checked before any mutation, so a forbidden refresh leaves the presented token usable. Whether a status
        // change should revoke existing sessions is a separate, deferred decision.
        if (user.Status != UserStatus.Active)
        {
            return Result<RefreshResult>.Failure(RefreshErrors.AccountUnavailable);
        }

        // Token creation happens before any tracked change, so a failure here persists nothing.
        var accessToken = tokenProvider.CreateAccessToken(user.Id);
        var generatedRefreshToken = tokenProvider.CreateRefreshToken();

        // The presented token is tracked by the shared context, so its revocation commits with the replacement.
        refreshToken.Revoke(refreshedAt);

        var replacementToken = new RefreshToken(
            Guid.NewGuid(),
            user.Id,
            generatedRefreshToken.TokenHash,
            refreshedAt,
            generatedRefreshToken.ExpiresAt);

        await refreshTokenRepository.AddAsync(replacementToken, cancellationToken);

        try
        {
            // Revocation and insertion commit together or not at all.
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }
        catch (ConcurrencyConflictException exception)
            when (exception.Resource == PersistenceResource.RefreshToken)
        {
            // Another request revoked this token first, so the save was rejected as a whole and the replacement
            // was not inserted. The presented credential is spent, which is the same outcome as a replayed token.
            // The request scope ends here, so the losing context's tracked changes are discarded with it.
            return Result<RefreshResult>.Failure(RefreshErrors.InvalidRefreshToken);
        }

        return Result<RefreshResult>.Success(new RefreshResult(
            accessToken.Value,
            accessToken.ExpiresAt,
            generatedRefreshToken.Value,
            generatedRefreshToken.ExpiresAt));
    }
}
