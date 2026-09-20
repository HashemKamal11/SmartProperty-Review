using SmartProperty.Application.Abstractions.Authentication;
using SmartProperty.Application.Abstractions.Messaging;
using SmartProperty.Application.Abstractions.Persistence;
using SmartProperty.Application.Abstractions.Persistence.Repositories;
using SmartProperty.Application.Abstractions.Time;
using SmartProperty.Common.Results;
using SmartProperty.Domain.Identity;

namespace SmartProperty.Application.Authentication.Login;

/// <summary>
/// Authenticates an active user by email and password and issues an access token and a refresh token.
/// Persists only the refresh token's hash, together with a password re-hash when one is needed.
/// </summary>
public sealed class LoginCommandHandler(
    IUserRepository userRepository,
    IUserCredentialRepository userCredentialRepository,
    IRefreshTokenRepository refreshTokenRepository,
    IPasswordHasher passwordHasher,
    ITokenProvider tokenProvider,
    IDateTimeProvider dateTimeProvider,
    IUnitOfWork unitOfWork)
    : ICommandHandler<LoginCommand, LoginResult>
{
    public async Task<Result<LoginResult>> Handle(
        LoginCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (LoginCommandValidator.Validate(command) is { } validationError)
        {
            return Result<LoginResult>.Failure(validationError);
        }

        // The repository normalizes the email the same way User stores it.
        var user = await userRepository.GetByEmailAsync(command.Email, cancellationToken);

        if (user is null)
        {
            // Spends the same password-hashing work a real attempt costs, so an unregistered email is not
            // distinguishable from a wrong password by response time alone.
            passwordHasher.PerformDummyVerification(command.Password);

            return Result<LoginResult>.Failure(LoginErrors.InvalidCredentials);
        }

        var credential = await userCredentialRepository.GetByUserIdAsync(user.Id, cancellationToken);

        if (credential is null)
        {
            passwordHasher.PerformDummyVerification(command.Password);

            return Result<LoginResult>.Failure(LoginErrors.InvalidCredentials);
        }

        var verification = passwordHasher.Verify(command.Password, credential.PasswordHash);

        // Anything other than a successful verification, including an unrecognized value, fails closed.
        if (verification is not (PasswordVerificationStatus.Success or PasswordVerificationStatus.SuccessRehashNeeded))
        {
            return Result<LoginResult>.Failure(LoginErrors.InvalidCredentials);
        }

        // Checked only after the password verified, so the account status is never revealed to a caller who does
        // not know the password. Nothing is changed for a rejected login, not even a needed re-hash.
        if (user.Status != UserStatus.Active)
        {
            return Result<LoginResult>.Failure(LoginErrors.AccountUnavailable);
        }

        var loggedInAt = dateTimeProvider.UtcNow;

        // The credential is tracked by the shared context, so the new hash is committed with the refresh token.
        if (verification == PasswordVerificationStatus.SuccessRehashNeeded)
        {
            credential.ChangePasswordHash(passwordHasher.Hash(command.Password), loggedInAt);
        }

        var accessToken = tokenProvider.CreateAccessToken(user.Id);
        var generatedRefreshToken = tokenProvider.CreateRefreshToken();

        var refreshToken = new RefreshToken(
            Guid.NewGuid(),
            user.Id,
            generatedRefreshToken.TokenHash,
            loggedInAt,
            generatedRefreshToken.ExpiresAt);

        await refreshTokenRepository.AddAsync(refreshToken, cancellationToken);

        // Tokens are returned only after the save succeeds, so a failed save never hands out a usable refresh token.
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return Result<LoginResult>.Success(new LoginResult(
            accessToken.Value,
            accessToken.ExpiresAt,
            generatedRefreshToken.Value,
            generatedRefreshToken.ExpiresAt));
    }
}
