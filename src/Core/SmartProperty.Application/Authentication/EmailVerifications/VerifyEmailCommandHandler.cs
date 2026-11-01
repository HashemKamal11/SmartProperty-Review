using SmartProperty.Application.Abstractions.Messaging;
using SmartProperty.Application.Abstractions.Persistence;
using SmartProperty.Application.Abstractions.Persistence.Repositories;
using SmartProperty.Application.Abstractions.Time;
using SmartProperty.Application.Authentication.OneTimeCodes;
using SmartProperty.Common.Results;
using SmartProperty.Domain.Identity;

namespace SmartProperty.Application.Authentication.EmailVerifications;

/// <summary>
/// Checks a submitted code against the latest email verification code of the account with this email and marks the
/// email verified, proving the address belongs to the person registering. Grants no workspace access and issues no
/// tokens.
/// </summary>
/// <remarks>
/// An unknown email, an account with nothing left to verify, and a wrong code all return the same error, so the
/// endpoint reveals nothing about which emails are registered.
/// </remarks>
public sealed class VerifyEmailCommandHandler(
    IUserRepository userRepository,
    IOneTimeCodeRepository oneTimeCodeRepository,
    IDateTimeProvider dateTimeProvider,
    IUnitOfWork unitOfWork)
    : ICommandHandler<VerifyEmailCommand, EmailVerified>
{
    private const OneTimeCodePurpose Purpose = OneTimeCodePurpose.EmailVerification;

    public async Task<Result<EmailVerified>> Handle(
        VerifyEmailCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (CodeRecipient.ValidateEmail(command.Email) is { } validationError)
        {
            return Result<EmailVerified>.Failure(validationError);
        }

        var user = await userRepository.GetByEmailAsync(command.Email, cancellationToken);

        if (user is null || !CodeRecipient.CanReceiveCodes(user) || user.IsEmailVerified)
        {
            return Result<EmailVerified>.Failure(OneTimeCodeErrors.InvalidOrExpired(Purpose));
        }

        var now = dateTimeProvider.UtcNow;
        var check = await OneTimeCodeFlow.CheckAsync(
            oneTimeCodeRepository,
            unitOfWork,
            user.Id,
            Purpose,
            command.Code,
            now,
            cancellationToken);

        if (check != CodeCheck.Confirmed)
        {
            return Result<EmailVerified>.Failure(OneTimeCodeErrors.ForCheck(Purpose, check));
        }

        user.VerifyEmail(now);

        if (!await OneTimeCodeFlow.TrySaveAsync(unitOfWork, cancellationToken))
        {
            return Result<EmailVerified>.Failure(OneTimeCodeErrors.InvalidOrExpired(Purpose));
        }

        return Result<EmailVerified>.Success(new EmailVerified(user.Id, now, user.Status));
    }
}
