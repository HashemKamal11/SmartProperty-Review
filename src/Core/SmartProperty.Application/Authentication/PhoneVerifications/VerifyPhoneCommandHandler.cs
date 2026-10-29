using SmartProperty.Application.Abstractions.Messaging;
using SmartProperty.Application.Abstractions.Persistence;
using SmartProperty.Application.Abstractions.Persistence.Repositories;
using SmartProperty.Application.Abstractions.Time;
using SmartProperty.Common.Results;
using SmartProperty.Domain.Identity;

namespace SmartProperty.Application.Authentication.PhoneVerifications;

/// <summary>
/// Checks a submitted code against the user's latest verification code and marks the phone number verified.
/// A code allows <see cref="PhoneVerification.MaxFailedAttempts"/> wrong guesses, then is locked.
/// Verifying a phone grants no workspace access and issues no tokens.
/// </summary>
public sealed class VerifyPhoneCommandHandler(
    IUserRepository userRepository,
    IPhoneVerificationRepository phoneVerificationRepository,
    IDateTimeProvider dateTimeProvider,
    IUnitOfWork unitOfWork)
    : ICommandHandler<VerifyPhoneCommand, PhoneVerified>
{
    public async Task<Result<PhoneVerified>> Handle(
        VerifyPhoneCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (command.UserId == Guid.Empty)
        {
            return Result<PhoneVerified>.Failure(PhoneVerificationErrors.ValidationFailed("User is required."));
        }

        // A malformed code cannot match, so it is refused without spending one of the code's attempts.
        if (!PhoneVerificationPolicy.IsWellFormedCode(command.Code))
        {
            return Result<PhoneVerified>.Failure(PhoneVerificationErrors.InvalidOrExpiredCode);
        }

        var user = await userRepository.GetByIdAsync(command.UserId, cancellationToken);

        if (user is null || user.PhoneNumber is null)
        {
            return Result<PhoneVerified>.Failure(PhoneVerificationErrors.UserNotFound);
        }

        if (user.IsPhoneVerified)
        {
            return Result<PhoneVerified>.Failure(PhoneVerificationErrors.AlreadyVerified);
        }

        var now = dateTimeProvider.UtcNow;
        var verification = await phoneVerificationRepository.GetLatestByUserIdAsync(user.Id, cancellationToken);

        if (verification is null || verification.ConsumedAt is not null || now >= verification.ExpiresAt)
        {
            return Result<PhoneVerified>.Failure(PhoneVerificationErrors.InvalidOrExpiredCode);
        }

        if (!verification.IsActive(now))
        {
            return Result<PhoneVerified>.Failure(PhoneVerificationErrors.TooManyAttempts);
        }

        if (!PhoneVerificationPolicy.CodeMatches(user.Id, command.Code, verification.CodeHash))
        {
            verification.RegisterFailedAttempt();

            try
            {
                await unitOfWork.SaveChangesAsync(cancellationToken);
            }
            catch (ConcurrencyConflictException exception)
                when (exception.Resource == PersistenceResource.PhoneVerification)
            {
                // A concurrent guess counted first; this one is refused like any other wrong code.
                return Result<PhoneVerified>.Failure(PhoneVerificationErrors.InvalidOrExpiredCode);
            }

            return Result<PhoneVerified>.Failure(verification.IsActive(now)
                ? PhoneVerificationErrors.InvalidOrExpiredCode
                : PhoneVerificationErrors.TooManyAttempts);
        }

        verification.Consume(now);
        user.VerifyPhone(now);

        try
        {
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }
        catch (ConcurrencyConflictException exception)
            when (exception.Resource == PersistenceResource.PhoneVerification)
        {
            return Result<PhoneVerified>.Failure(PhoneVerificationErrors.InvalidOrExpiredCode);
        }

        return Result<PhoneVerified>.Success(new PhoneVerified(user.Id, now, user.Status));
    }
}
