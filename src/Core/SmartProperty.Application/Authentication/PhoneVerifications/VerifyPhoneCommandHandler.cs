using SmartProperty.Application.Abstractions.Messaging;
using SmartProperty.Application.Abstractions.Persistence;
using SmartProperty.Application.Abstractions.Persistence.Repositories;
using SmartProperty.Application.Abstractions.Time;
using SmartProperty.Application.Authentication.OneTimeCodes;
using SmartProperty.Common.Results;
using SmartProperty.Domain.Identity;

namespace SmartProperty.Application.Authentication.PhoneVerifications;

/// <summary>
/// Checks a submitted code against the latest phone verification code of the account with this phone number and marks
/// it verified. A code allows <see cref="OneTimeCode.MaxFailedAttempts"/> wrong guesses, then is locked.
/// Verifying the last unverified contact activates a Pending account; verifying a phone grants no workspace access and issues no tokens.
/// </summary>
/// <remarks>
/// The account is found by phone number, which anyone can type, so an unknown number, an account with nothing left to
/// verify, and a wrong code all return the same error.
/// </remarks>
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

        if (CodeRecipient.ValidatePhoneNumber(command.PhoneNumber, out var phoneNumber) is { } validationError)
        {
            return Result<PhoneVerified>.Failure(validationError);
        }

        var user = await userRepository.GetByPhoneNumberAsync(phoneNumber, cancellationToken);

        if (user is null
            || !CodeRecipient.CanReceiveCodes(user)
            || user.IsPhoneVerified)
        {
            return Result<PhoneVerified>.Failure(OneTimeCodeErrors.InvalidOrExpired(Purpose));
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
        user.ActivateWhenContactVerified(now);

        if (!await OneTimeCodeFlow.TrySaveAsync(unitOfWork, cancellationToken))
        {
            return Result<PhoneVerified>.Failure(OneTimeCodeErrors.InvalidOrExpired(Purpose));
        }

        return Result<PhoneVerified>.Success(new PhoneVerified(user.Id, now, user.Status));
    }
}
