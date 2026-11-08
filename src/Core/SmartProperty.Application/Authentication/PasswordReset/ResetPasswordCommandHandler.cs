using SmartProperty.Application.Abstractions.Authentication;
using SmartProperty.Application.Abstractions.Messaging;
using SmartProperty.Application.Abstractions.Persistence;
using SmartProperty.Application.Abstractions.Persistence.Repositories;
using SmartProperty.Application.Abstractions.Time;
using SmartProperty.Application.Authentication.OneTimeCodes;
using SmartProperty.Application.Authentication.Register;
using SmartProperty.Common.Results;
using SmartProperty.Domain.Identity;

namespace SmartProperty.Application.Authentication.PasswordReset;

/// <summary>
/// Sets a new password when the submitted code matches the latest password reset code emailed to the account, and
/// signs the account out everywhere by revoking all of its active refresh tokens. The password change, the used
/// code, and the revocations are saved together, so a reset never half-applies.
/// </summary>
/// <remarks>
/// An unknown email, a disabled account, a wrong code, and an expired code all return the same error, so the reset
/// endpoint reveals nothing about which emails are registered. Access tokens already issued stay valid until they
/// expire, as documented for logout.
/// </remarks>
public sealed class ResetPasswordCommandHandler(
    IUserRepository userRepository,
    IUserCredentialRepository userCredentialRepository,
    IRefreshTokenRepository refreshTokenRepository,
    IOneTimeCodeRepository oneTimeCodeRepository,
    IPasswordHasher passwordHasher,
    IDateTimeProvider dateTimeProvider,
    IUnitOfWork unitOfWork)
    : ICommandHandler<ResetPasswordCommand>
{
    private const OneTimeCodePurpose GrantPurpose = OneTimeCodePurpose.PasswordResetGrant;

    private static readonly Error TryAgain = new(
        "password_reset.conflict",
        "The password could not be changed because the account was updated at the same time. Try again.",
        ErrorType.Conflict);

    public async Task<Result> Handle(
        ResetPasswordCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (CodeRecipient.ValidateEmail(command.Email) is { } emailError)
        {
            return Result.Failure(emailError);
        }

        // The password is hashed exactly as sent, so it is not trimmed here.
        if (string.IsNullOrWhiteSpace(command.NewPassword))
        {
            return Result.Failure(OneTimeCodeErrors.ValidationFailed("New password is required."));
        }

        if (command.NewPassword.Length < RegisterCommandValidator.PasswordMinLength)
        {
            return Result.Failure(OneTimeCodeErrors.ValidationFailed(
                $"New password must be at least {RegisterCommandValidator.PasswordMinLength} characters."));
        }

        if (command.NewPassword.Length > RegisterCommandValidator.PasswordMaxLength)
        {
            return Result.Failure(OneTimeCodeErrors.ValidationFailed(
                $"New password must not exceed {RegisterCommandValidator.PasswordMaxLength} characters."));
        }

        if (!string.Equals(command.NewPassword, command.ConfirmPassword, StringComparison.Ordinal))
        {
            return Result.Failure(OneTimeCodeErrors.ValidationFailed("The two passwords do not match."));
        }

        var user = await userRepository.GetByEmailAsync(command.Email, cancellationToken);

        if (user is null || !CodeRecipient.CanReceiveCodes(user))
        {
            // Pays the same hashing cost as a real reset, so response time does not reveal an unknown email.
            passwordHasher.PerformDummyVerification(command.NewPassword);

            return Result.Failure(OneTimeCodeErrors.InvalidResetToken);
        }

        var now = dateTimeProvider.UtcNow;

        // The token is 256 random bits, so a wrong one is not worth counting attempts against; it is simply refused.
        var grant = await oneTimeCodeRepository.GetLatestAsync(user.Id, GrantPurpose, cancellationToken);

        if (!OneTimeCodePolicy.IsWellFormedGrantToken(command.ResetToken)
            || grant is null
            || !grant.IsActive(now)
            || !OneTimeCodePolicy.CodeMatches(user.Id, GrantPurpose, command.ResetToken, grant.CodeHash))
        {
            passwordHasher.PerformDummyVerification(command.NewPassword);

            return Result.Failure(OneTimeCodeErrors.InvalidResetToken);
        }

        grant.Consume(now);

        var credential = await userCredentialRepository.GetByUserIdAsync(user.Id, cancellationToken);

        if (credential is null)
        {
            return Result.Failure(OneTimeCodeErrors.InvalidResetToken);
        }

        credential.ChangePasswordHash(passwordHasher.Hash(command.NewPassword), now);

        foreach (var refreshToken in await refreshTokenRepository.GetActiveByUserIdAsync(
            user.Id,
            now,
            cancellationToken))
        {
            refreshToken.Revoke(now);
        }

        try
        {
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }
        catch (ConcurrencyConflictException)
        {
            // The whole save was rejected, so the code is not spent and the password is unchanged.
            return Result.Failure(TryAgain);
        }

        return Result.Success();
    }
}
