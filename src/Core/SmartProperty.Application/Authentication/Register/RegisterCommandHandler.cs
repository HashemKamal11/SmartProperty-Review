using SmartProperty.Application.Abstractions.Authentication;
using SmartProperty.Application.Abstractions.Messaging;
using SmartProperty.Application.Abstractions.Notifications;
using SmartProperty.Application.Abstractions.Persistence;
using SmartProperty.Application.Abstractions.Persistence.Repositories;
using SmartProperty.Application.Abstractions.Time;
using SmartProperty.Common.Results;

namespace SmartProperty.Application.Authentication.Register;

/// <summary>
/// Registers a Pending user with a credential and an unverified phone number and email, then sends a verification
/// code to the phone number and another to the email. The account cannot sign in until both are verified; verifying
/// the last one activates it, with no administrator involved. Registration is not tied to a workspace: it creates no
/// access request, grants no workspace access, roles, or permissions, and issues no tokens.
/// </summary>
public sealed class RegisterCommandHandler(
    IUserRepository userRepository,
    IUserCredentialRepository userCredentialRepository,
    IOneTimeCodeRepository oneTimeCodeRepository,
    IPasswordHasher passwordHasher,
    ISmsSender smsSender,
    IEmailSender emailSender,
    IContactVerificationPolicy contactVerificationPolicy,
    IDateTimeProvider dateTimeProvider,
    IUnitOfWork unitOfWork)
    : ICommandHandler<RegisterCommand, RegisterResult>
{
    private readonly PendingAccountRegistrar _registrar = new(
        userRepository,
        userCredentialRepository,
        oneTimeCodeRepository,
        passwordHasher,
        smsSender,
        emailSender,
        contactVerificationPolicy);

    public async Task<Result<RegisterResult>> Handle(
        RegisterCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (RegisterCommandValidator.Validate(command) is { } validationError)
        {
            return Result<RegisterResult>.Failure(validationError);
        }

        var staged = await _registrar.StageAsync(command, dateTimeProvider.UtcNow, cancellationToken);

        if (staged.IsFailure)
        {
            return Result<RegisterResult>.Failure(staged.Error!);
        }

        try
        {
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }
        catch (UniqueConstraintViolationException exception)
            when (PendingAccountRegistrar.TranslateSaveCollision(exception) is not null)
        {
            // The save was rejected as a whole, so the outcome matches the duplicate checks.
            return Result<RegisterResult>.Failure(PendingAccountRegistrar.TranslateSaveCollision(exception)!);
        }

        var account = staged.Value;
        await _registrar.SendCodesAsync(account, cancellationToken);

        return Result<RegisterResult>.Success(new RegisterResult(
            account.User.Id,
            account.User.Status,
            account.PhoneVerificationExpiresAt,
            account.EmailVerificationExpiresAt));
    }
}
