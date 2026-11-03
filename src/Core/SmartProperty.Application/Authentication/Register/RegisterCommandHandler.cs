using SmartProperty.Application.Abstractions.Authentication;
using SmartProperty.Application.Abstractions.Messaging;
using SmartProperty.Application.Abstractions.Notifications;
using SmartProperty.Application.Abstractions.Persistence;
using SmartProperty.Application.Abstractions.Persistence.Repositories;
using SmartProperty.Application.Abstractions.Time;
using SmartProperty.Application.Authentication.OneTimeCodes;
using SmartProperty.Common.Results;
using SmartProperty.Domain.Identity;

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
    IDateTimeProvider dateTimeProvider,
    IUnitOfWork unitOfWork)
    : ICommandHandler<RegisterCommand, RegisterResult>
{
    public async Task<Result<RegisterResult>> Handle(
        RegisterCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (RegisterCommandValidator.Validate(command) is { } validationError)
        {
            return Result<RegisterResult>.Failure(validationError);
        }

        var registeredAt = dateTimeProvider.UtcNow;

        // Constructed before the duplicate checks so the lookups use the email and phone number exactly as User
        // normalizes and stores them. Nothing is tracked until every check has passed.
        var user = new User(
            Guid.NewGuid(),
            command.Email,
            command.FirstName,
            command.LastName,
            registeredAt,
            command.PhoneNumber);

        // Rejects an already registered email or phone number before hashing. A concurrent registration can
        // still pass these checks; the unique indexes then reject the save, which is handled below.
        if (await userRepository.GetByEmailAsync(user.Email, cancellationToken) is not null)
        {
            return Result<RegisterResult>.Failure(RegisterErrors.EmailAlreadyExists);
        }

        if (await userRepository.GetByPhoneNumberAsync(user.PhoneNumber!, cancellationToken) is not null)
        {
            return Result<RegisterResult>.Failure(RegisterErrors.PhoneNumberAlreadyExists);
        }

        var credential = new UserCredential(user.Id, passwordHasher.Hash(command.Password), registeredAt);
        var phoneCode = OneTimeCodePolicy.GenerateCode();
        var phoneVerification = OneTimeCodePolicy.Issue(
            user.Id,
            OneTimeCodePurpose.PhoneVerification,
            phoneCode,
            registeredAt);
        var emailCode = OneTimeCodePolicy.GenerateCode();
        var emailVerification = OneTimeCodePolicy.Issue(
            user.Id,
            OneTimeCodePurpose.EmailVerification,
            emailCode,
            registeredAt);

        await userRepository.AddAsync(user, cancellationToken);
        await userCredentialRepository.AddAsync(credential, cancellationToken);
        await oneTimeCodeRepository.AddAsync(phoneVerification, cancellationToken);
        await oneTimeCodeRepository.AddAsync(emailVerification, cancellationToken);

        try
        {
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }
        catch (UniqueConstraintViolationException exception)
            when (exception.Constraint == PersistenceConstraint.UserEmail)
        {
            // The save was rejected as a whole, so the outcome matches the duplicate check above.
            return Result<RegisterResult>.Failure(RegisterErrors.EmailAlreadyExists);
        }
        catch (UniqueConstraintViolationException exception)
            when (exception.Constraint == PersistenceConstraint.UserPhoneNumber)
        {
            return Result<RegisterResult>.Failure(RegisterErrors.PhoneNumberAlreadyExists);
        }

        // Sent only after the registration is committed, so a rejected registration never texts or emails anyone.
        // If a send fails the account still exists, and the caller can request another code.
        await smsSender.SendAsync(user.PhoneNumber!, OneTimeCodePolicy.BuildPhoneMessage(phoneCode), cancellationToken);

        var (subject, body) = OneTimeCodePolicy.BuildEmailVerificationMessage(emailCode);
        await emailSender.SendAsync(user.Email, subject, body, cancellationToken);

        return Result<RegisterResult>.Success(new RegisterResult(
            user.Id,
            user.Status,
            phoneVerification.ExpiresAt,
            emailVerification.ExpiresAt));
    }
}
