using SmartProperty.Application.Abstractions.Authentication;
using SmartProperty.Application.Abstractions.Messaging;
using SmartProperty.Application.Abstractions.Notifications;
using SmartProperty.Application.Abstractions.Persistence;
using SmartProperty.Application.Abstractions.Persistence.Repositories;
using SmartProperty.Application.Abstractions.Time;
using SmartProperty.Application.Authentication.Register;
using SmartProperty.Common.Results;
using SmartProperty.Domain.Workspaces;

namespace SmartProperty.Application.WorkspaceRegistrations.Register;

/// <summary>
/// Company sign-up. Registers the applicant exactly as individual registration does (Pending account, verification
/// codes to the phone and the email) and, in the same save, a Pending workspace registration request. Nothing is
/// granted: the workspace exists only once a platform administrator approves the request.
/// </summary>
public sealed class RegisterWorkspaceCommandHandler(
    IUserRepository userRepository,
    IUserCredentialRepository userCredentialRepository,
    IOneTimeCodeRepository oneTimeCodeRepository,
    IWorkspaceRegistrationRequestRepository registrationRequests,
    IPasswordHasher passwordHasher,
    ISmsSender smsSender,
    IEmailSender emailSender,
    IContactVerificationPolicy contactVerificationPolicy,
    IDateTimeProvider dateTimeProvider,
    IUnitOfWork unitOfWork)
    : ICommandHandler<RegisterWorkspaceCommand, RegisterWorkspaceResult>
{
    private readonly PendingAccountRegistrar _registrar = new(
        userRepository,
        userCredentialRepository,
        oneTimeCodeRepository,
        passwordHasher,
        smsSender,
        emailSender,
        contactVerificationPolicy);

    public async Task<Result<RegisterWorkspaceResult>> Handle(
        RegisterWorkspaceCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        var account = new RegisterCommand(
            command.Email,
            command.Password,
            command.FirstName,
            command.LastName,
            command.PhoneNumber);

        var validationError = RegisterCommandValidator.Validate(account)
            ?? CompanyDetailsValidator.Validate(command.CompanyName, command.CommercialRegistrationNumber);

        if (validationError is not null)
        {
            return Result<RegisterWorkspaceResult>.Failure(validationError);
        }

        var now = dateTimeProvider.UtcNow;
        var staged = await _registrar.StageAsync(account, now, cancellationToken);

        if (staged.IsFailure)
        {
            return Result<RegisterWorkspaceResult>.Failure(staged.Error!);
        }

        var request = new WorkspaceRegistrationRequest(
            Guid.NewGuid(),
            staged.Value.User.Id,
            command.CompanyName,
            command.CommercialRegistrationNumber,
            now);

        await registrationRequests.AddAsync(request, cancellationToken);

        try
        {
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }
        catch (UniqueConstraintViolationException exception)
            when (PendingAccountRegistrar.TranslateSaveCollision(exception) is not null)
        {
            // A concurrent registration took the email or phone number; the save was rejected as a whole.
            return Result<RegisterWorkspaceResult>.Failure(PendingAccountRegistrar.TranslateSaveCollision(exception)!);
        }

        await _registrar.SendCodesAsync(staged.Value, cancellationToken);

        return Result<RegisterWorkspaceResult>.Success(new RegisterWorkspaceResult(
            staged.Value.User.Id,
            staged.Value.User.Status,
            staged.Value.PhoneVerificationExpiresAt,
            staged.Value.EmailVerificationExpiresAt,
            request.Id,
            request.Status));
    }
}
