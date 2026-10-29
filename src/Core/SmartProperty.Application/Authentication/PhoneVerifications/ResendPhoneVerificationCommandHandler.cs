using SmartProperty.Application.Abstractions.Messaging;
using SmartProperty.Application.Abstractions.Notifications;
using SmartProperty.Application.Abstractions.Persistence;
using SmartProperty.Application.Abstractions.Persistence.Repositories;
using SmartProperty.Application.Abstractions.Time;
using SmartProperty.Common.Results;

namespace SmartProperty.Application.Authentication.PhoneVerifications;

/// <summary>
/// Sends a fresh verification code to a user whose phone number is not verified yet. Earlier unused codes are
/// retired so only the newest one works, and a new code cannot be requested within
/// <see cref="PhoneVerificationPolicy.ResendCooldown"/> of the previous one.
/// </summary>
public sealed class ResendPhoneVerificationCommandHandler(
    IUserRepository userRepository,
    IPhoneVerificationRepository phoneVerificationRepository,
    ISmsSender smsSender,
    IDateTimeProvider dateTimeProvider,
    IUnitOfWork unitOfWork)
    : ICommandHandler<ResendPhoneVerificationCommand, PhoneVerificationIssued>
{
    public async Task<Result<PhoneVerificationIssued>> Handle(
        ResendPhoneVerificationCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (command.UserId == Guid.Empty)
        {
            return Result<PhoneVerificationIssued>.Failure(PhoneVerificationErrors.ValidationFailed("User is required."));
        }

        var user = await userRepository.GetByIdAsync(command.UserId, cancellationToken);

        if (user is null || user.PhoneNumber is null)
        {
            return Result<PhoneVerificationIssued>.Failure(PhoneVerificationErrors.UserNotFound);
        }

        if (user.IsPhoneVerified)
        {
            return Result<PhoneVerificationIssued>.Failure(PhoneVerificationErrors.AlreadyVerified);
        }

        var now = dateTimeProvider.UtcNow;
        var latest = await phoneVerificationRepository.GetLatestByUserIdAsync(user.Id, cancellationToken);

        if (latest is not null && now < latest.CreatedAt.Add(PhoneVerificationPolicy.ResendCooldown))
        {
            return Result<PhoneVerificationIssued>.Failure(PhoneVerificationErrors.ResendTooSoon);
        }

        foreach (var previous in await phoneVerificationRepository.GetUnconsumedByUserIdAsync(user.Id, cancellationToken))
        {
            previous.Consume(now);
        }

        var code = PhoneVerificationPolicy.GenerateCode();
        var verification = PhoneVerificationPolicy.Issue(user.Id, code, now);

        await phoneVerificationRepository.AddAsync(verification, cancellationToken);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        await smsSender.SendAsync(user.PhoneNumber, PhoneVerificationPolicy.BuildMessage(code), cancellationToken);

        return Result<PhoneVerificationIssued>.Success(new PhoneVerificationIssued(verification.ExpiresAt));
    }
}
