using SmartProperty.Application.Abstractions.Messaging;
using SmartProperty.Application.Abstractions.Persistence;
using SmartProperty.Application.Abstractions.Persistence.Repositories;
using SmartProperty.Application.Abstractions.Time;
using SmartProperty.Application.Authentication.OneTimeCodes;
using SmartProperty.Common.Results;
using SmartProperty.Domain.Identity;

namespace SmartProperty.Application.Authentication.EmailVerifications;

/// <summary>
/// Checks a submitted code against the latest email verification code of the user and marks the email verified,
/// proving the address belongs to the person registering. Grants no workspace access and issues no tokens.
/// </summary>
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

        if (command.UserId == Guid.Empty)
        {
            return Result<EmailVerified>.Failure(OneTimeCodeErrors.ValidationFailed("User is required."));
        }

        var user = await userRepository.GetByIdAsync(command.UserId, cancellationToken);

        if (user is null)
        {
            return Result<EmailVerified>.Failure(OneTimeCodeErrors.UserNotFound(Purpose));
        }

        if (user.IsEmailVerified)
        {
            return Result<EmailVerified>.Failure(OneTimeCodeErrors.AlreadyVerified(Purpose));
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
