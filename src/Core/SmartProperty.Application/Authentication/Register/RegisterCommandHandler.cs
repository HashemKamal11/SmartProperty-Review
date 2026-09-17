using SmartProperty.Application.Abstractions.Authentication;
using SmartProperty.Application.Abstractions.Messaging;
using SmartProperty.Application.Abstractions.Persistence;
using SmartProperty.Application.Abstractions.Persistence.Repositories;
using SmartProperty.Application.Abstractions.Time;
using SmartProperty.Common.Results;
using SmartProperty.Domain.Identity;
using SmartProperty.Domain.Workspaces;

namespace SmartProperty.Application.Authentication.Register;

/// <summary>
/// Registers a pending user with a credential and one pending access request for an existing workspace.
/// Grants no workspace access, roles, or permissions, and issues no tokens.
/// </summary>
public sealed class RegisterCommandHandler(
    IUserRepository userRepository,
    IUserCredentialRepository userCredentialRepository,
    IWorkspaceRepository workspaceRepository,
    IWorkspaceAccessRequestRepository workspaceAccessRequestRepository,
    IPasswordHasher passwordHasher,
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

        // Checked before the email, so a request with an unknown workspace reveals nothing about registered emails.
        var workspace = await workspaceRepository.GetByIdAsync(command.WorkspaceId, cancellationToken);

        if (workspace is null)
        {
            return Result<RegisterResult>.Failure(RegisterErrors.WorkspaceNotFound);
        }

        var registeredAt = dateTimeProvider.UtcNow;

        // Constructed before the duplicate check so the lookup uses the email exactly as User normalizes and
        // stores it. Nothing is tracked until every check has passed.
        var user = new User(Guid.NewGuid(), command.Email, command.FirstName, command.LastName, registeredAt);

        // Rejects an already registered email before hashing. A concurrent registration can still pass this check;
        // the unique email index then rejects the save, which is handled below.
        if (await userRepository.GetByEmailAsync(user.Email, cancellationToken) is not null)
        {
            return Result<RegisterResult>.Failure(RegisterErrors.EmailAlreadyExists);
        }

        var credential = new UserCredential(user.Id, passwordHasher.Hash(command.Password), registeredAt);
        var accessRequest = new WorkspaceAccessRequest(Guid.NewGuid(), user.Id, workspace.Id, registeredAt);

        await userRepository.AddAsync(user, cancellationToken);
        await userCredentialRepository.AddAsync(credential, cancellationToken);
        await workspaceAccessRequestRepository.AddAsync(accessRequest, cancellationToken);

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

        return Result<RegisterResult>.Success(new RegisterResult(
            user.Id,
            user.Status,
            accessRequest.WorkspaceId,
            accessRequest.Id,
            accessRequest.Status));
    }
}
