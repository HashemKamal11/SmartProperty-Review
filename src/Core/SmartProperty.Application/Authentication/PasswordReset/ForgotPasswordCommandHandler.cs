using SmartProperty.Application.Abstractions.Messaging;
using SmartProperty.Application.Abstractions.Notifications;
using SmartProperty.Application.Abstractions.Persistence;
using SmartProperty.Application.Abstractions.Persistence.Repositories;
using SmartProperty.Application.Abstractions.Time;
using SmartProperty.Application.Authentication.OneTimeCodes;
using SmartProperty.Common.Results;
using SmartProperty.Domain.Identity;

namespace SmartProperty.Application.Authentication.PasswordReset;

/// <summary>
/// Emails a password reset code to the address, when an account that can sign in again uses it.
/// </summary>
/// <remarks>
/// The answer never depends on the account: an unknown email, a Suspended or Deactivated account, and a request
/// inside the resend cooldown all succeed silently, so the endpoint cannot be used to find out which emails are
/// registered. Only a malformed email is refused, which says nothing about any account. A Pending account can
/// reset its password, because it can still register and verify.
/// </remarks>
public sealed class ForgotPasswordCommandHandler(
    IUserRepository userRepository,
    IOneTimeCodeRepository oneTimeCodeRepository,
    IEmailSender emailSender,
    IDateTimeProvider dateTimeProvider,
    IUnitOfWork unitOfWork)
    : ICommandHandler<ForgotPasswordCommand>
{
    private const OneTimeCodePurpose Purpose = OneTimeCodePurpose.PasswordReset;

    public async Task<Result> Handle(
        ForgotPasswordCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (CodeRecipient.ValidateEmail(command.Email) is { } validationError)
        {
            return Result.Failure(validationError);
        }

        var user = await userRepository.GetByEmailAsync(command.Email, cancellationToken);

        if (user is null || !CodeRecipient.CanReceiveCodes(user))
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

        var (subject, body) = OneTimeCodePolicy.BuildPasswordResetMessage(issued.Value.Code);
        await emailSender.SendAsync(user.Email, subject, body, cancellationToken);

        return Result.Success();
    }
}
