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
/// Sends a fresh verification code to a user whose phone number is not verified yet. Earlier unused codes are
/// retired so only the newest one works, and a new code cannot be requested within
/// <see cref="OneTimeCodePolicy.ResendCooldown"/> of the previous one.
/// </summary>
public sealed class ResendPhoneVerificationCommandHandler(
    IUserRepository userRepository,
    IOneTimeCodeRepository oneTimeCodeRepository,
    ISmsSender smsSender,
    IDateTimeProvider dateTimeProvider,
    IUnitOfWork unitOfWork)
    : ICommandHandler<ResendPhoneVerificationCommand, OneTimeCodeIssued>
{
    private const OneTimeCodePurpose Purpose = OneTimeCodePurpose.PhoneVerification;

    public async Task<Result<OneTimeCodeIssued>> Handle(
        ResendPhoneVerificationCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (command.UserId == Guid.Empty)
        {
            return Result<OneTimeCodeIssued>.Failure(OneTimeCodeErrors.ValidationFailed("User is required."));
        }

        var user = await userRepository.GetByIdAsync(command.UserId, cancellationToken);

        if (user is null || user.PhoneNumber is null)
        {
            return Result<OneTimeCodeIssued>.Failure(OneTimeCodeErrors.UserNotFound(Purpose));
        }

        if (user.IsPhoneVerified)
        {
            return Result<OneTimeCodeIssued>.Failure(OneTimeCodeErrors.AlreadyVerified(Purpose));
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
            return Result<OneTimeCodeIssued>.Failure(issued.Error!);
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);

        await smsSender.SendAsync(
            user.PhoneNumber,
            OneTimeCodePolicy.BuildPhoneMessage(issued.Value.Code),
            cancellationToken);

        return Result<OneTimeCodeIssued>.Success(new OneTimeCodeIssued(issued.Value.ExpiresAt));
    }
}
