using SmartProperty.Application.Abstractions.Messaging;
using SmartProperty.Application.Abstractions.Persistence;
using SmartProperty.Application.Abstractions.Persistence.Repositories;
using SmartProperty.Application.Abstractions.Time;
using SmartProperty.Application.Authentication.OneTimeCodes;
using SmartProperty.Common.Results;
using SmartProperty.Domain.Identity;

namespace SmartProperty.Application.Authentication.PhoneVerifications;

/// <summary>
/// Checks a submitted code against the latest phone verification code of the user and marks the phone number
/// verified. A code allows <see cref="OneTimeCode.MaxFailedAttempts"/> wrong guesses, then is locked.
/// Verifying a phone grants no workspace access and issues no tokens.
/// </summary>
public sealed class VerifyPhoneCommandHandler(
    IUserRepository userRepository,
    IOneTimeCodeRepository oneTimeCodeRepository,
    IDateTimeProvider dateTimeProvider,
    IUnitOfWork unitOfWork)
    : ICommandHandler<VerifyPhoneCommand, PhoneVerified>
{
    private const OneTimeCodePurpose Purpose = OneTimeCodePurpose.PhoneVerification;

    public async Task<Result<PhoneVerified>> Handle(
        VerifyPhoneCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (command.UserId == Guid.Empty)
        {
            return Result<PhoneVerified>.Failure(OneTimeCodeErrors.ValidationFailed("User is required."));
        }

        var user = await userRepository.GetByIdAsync(command.UserId, cancellationToken);

        if (user is null || user.PhoneNumber is null)
        {
            return Result<PhoneVerified>.Failure(OneTimeCodeErrors.UserNotFound(Purpose));
        }

        if (user.IsPhoneVerified)
        {
            return Result<PhoneVerified>.Failure(OneTimeCodeErrors.AlreadyVerified(Purpose));
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
            return Result<PhoneVerified>.Failure(OneTimeCodeErrors.ForCheck(Purpose, check));
        }

        user.VerifyPhone(now);

        if (!await OneTimeCodeFlow.TrySaveAsync(unitOfWork, cancellationToken))
        {
            return Result<PhoneVerified>.Failure(OneTimeCodeErrors.InvalidOrExpired(Purpose));
        }

        return Result<PhoneVerified>.Success(new PhoneVerified(user.Id, now, user.Status));
    }
}
