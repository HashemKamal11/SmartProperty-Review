using SmartProperty.Application.Abstractions.Messaging;
using SmartProperty.Application.Abstractions.Notifications;
using SmartProperty.Application.Abstractions.Persistence;
using SmartProperty.Application.Abstractions.Persistence.Repositories;
using SmartProperty.Application.Abstractions.Time;
using SmartProperty.Application.Authentication.OneTimeCodes;
using SmartProperty.Common.Results;
using SmartProperty.Domain.Identity;

namespace SmartProperty.Application.Authentication.EmailVerifications;

/// <summary>
/// Emails a fresh verification code to the account with this email, when that email is not verified yet. This is
/// how an applicant whose earlier code expired asks for another one. Earlier unused codes are retired so only the
/// newest one works.
/// </summary>
/// <remarks>
/// Works like forgot-password: the answer never depends on the account. An unknown email, an already verified
/// email, a disabled account, and a request inside the <see cref="OneTimeCodePolicy.ResendCooldown"/> all succeed
/// silently, so the endpoint cannot be used to find out which emails are registered. Only a malformed email is
/// refused.
/// </remarks>
public sealed class ResendEmailVerificationCommandHandler(
    IUserRepository userRepository,
    IOneTimeCodeRepository oneTimeCodeRepository,
    IEmailSender emailSender,
    IDateTimeProvider dateTimeProvider,
    IUnitOfWork unitOfWork)
    : ICommandHandler<ResendEmailVerificationCommand>
{
    private const OneTimeCodePurpose Purpose = OneTimeCodePurpose.EmailVerification;

    public async Task<Result> Handle(
        ResendEmailVerificationCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (CodeRecipient.ValidateEmail(command.Email) is { } validationError)
        {
            return Result.Failure(validationError);
        }

        var user = await userRepository.GetByEmailAsync(command.Email, cancellationToken);

        if (user is null || !CodeRecipient.CanReceiveCodes(user) || user.IsEmailVerified)
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

        var (subject, body) = OneTimeCodePolicy.BuildEmailVerificationMessage(issued.Value.Code);
        await emailSender.SendAsync(user.Email, subject, body, cancellationToken);

        return Result.Success();
    }
}
