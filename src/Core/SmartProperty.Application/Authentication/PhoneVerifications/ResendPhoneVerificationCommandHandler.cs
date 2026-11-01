using SmartProperty.Application.Abstractions.Messaging;
using SmartProperty.Application.Abstractions.Notifications;
using SmartProperty.Application.Abstractions.Persistence;
using SmartProperty.Application.Abstractions.Persistence.Repositories;
using SmartProperty.Application.Abstractions.Time;
using SmartProperty.Application.Authentication.OneTimeCodes;
using SmartProperty.Common.Results;
using SmartProperty.Domain.Identity;

namespace SmartProperty.Application.Authentication.PhoneVerifications;

/// <summary>
/// Sends a fresh verification code by SMS to the account with this phone number, when it is not verified
/// yet. This is how an applicant whose earlier code expired asks for another one. Earlier unused codes are retired
/// so only the newest one works.
/// </summary>
/// <remarks>
/// The answer never depends on the account: an unknown number, an account with a verified phone, a disabled
/// account, and a request inside the <see cref="OneTimeCodePolicy.ResendCooldown"/> all succeed silently, so the
/// endpoint cannot be used to find out which numbers are registered. Only a malformed number is refused.
/// </remarks>
public sealed class ResendPhoneVerificationCommandHandler(
    IUserRepository userRepository,
    IOneTimeCodeRepository oneTimeCodeRepository,
    ISmsSender smsSender,
    IDateTimeProvider dateTimeProvider,
    IUnitOfWork unitOfWork)
    : ICommandHandler<ResendPhoneVerificationCommand>
{
    private const OneTimeCodePurpose Purpose = OneTimeCodePurpose.PhoneVerification;

    public async Task<Result> Handle(
        ResendPhoneVerificationCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (CodeRecipient.ValidatePhoneNumber(command.PhoneNumber, out var phoneNumber) is { } validationError)
        {
            return Result.Failure(validationError);
        }

        var user = await userRepository.GetByPhoneNumberAsync(phoneNumber, cancellationToken);

        if (user is null
            || !CodeRecipient.CanReceiveCodes(user)
            || user.IsPhoneVerified)
        {
            return Result.Success();
        }

        var issued = await OneTimeCodeFlow.IssueAsync(
            oneTimeCodeRepository,
            user.Id,
            Purpose,
            dateTimeProvider.UtcNow,
            enforceCooldown: true,
            cancellationToken);

        if (issued.IsFailure)
        {
            return Result.Success();
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);

        await smsSender.SendAsync(
            phoneNumber,
            OneTimeCodePolicy.BuildPhoneMessage(issued.Value.Code),
            cancellationToken);

        return Result.Success();
    }
}
