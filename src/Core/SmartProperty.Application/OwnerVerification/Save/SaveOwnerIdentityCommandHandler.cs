using SmartProperty.Application.Abstractions.Messaging;
using SmartProperty.Application.Abstractions.Persistence;
using SmartProperty.Application.Abstractions.Persistence.Repositories;
using SmartProperty.Application.Abstractions.Time;
using SmartProperty.Common.Results;
using SmartProperty.Domain.OwnerVerification;

namespace SmartProperty.Application.OwnerVerification.Save;

/// <summary>Saves the national-ID step (step 2) of the signed-in user's owner profile.</summary>
public sealed record SaveOwnerIdentityCommand(
    Guid UserId,
    string? NationalId,
    string? Nationality,
    DateOnly? BirthDate,
    DateOnly? IdExpiryDate,
    bool Confirmed) : ICommand<OwnerProfileDetails>;

public sealed class SaveOwnerIdentityCommandHandler(
    IUserRepository userRepository,
    IOwnerProfileRepository profileRepository,
    OwnerAccessProvisioner accessProvisioner,
    IDateTimeProvider dateTimeProvider,
    IUnitOfWork unitOfWork)
    : ICommandHandler<SaveOwnerIdentityCommand, OwnerProfileDetails>
{
    public async Task<Result<OwnerProfileDetails>> Handle(
        SaveOwnerIdentityCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        var now = dateTimeProvider.UtcNow;

        var loaded = await OwnerProfileAccess.LoadEditableAsync(
            command.UserId,
            userRepository,
            profileRepository,
            () => new OwnerProfile(Guid.NewGuid(), command.UserId, now),
            cancellationToken);

        if (loaded.IsFailure)
        {
            return Result<OwnerProfileDetails>.Failure(loaded.Error!);
        }

        var (user, profile) = loaded.Value;

        if (command.BirthDate is null || command.IdExpiryDate is null)
        {
            unitOfWork.DiscardTrackedChanges();

            return Result<OwnerProfileDetails>.Failure(
                OwnerVerificationErrors.ValidationFailed("The birth date and the ID expiry date are required."));
        }

        if (OwnerProfile.IsValidNationalId(command.NationalId)
            && await profileRepository.NationalIdUsedByAnotherAsync(command.NationalId!, command.UserId, cancellationToken))
        {
            unitOfWork.DiscardTrackedChanges();

            return Result<OwnerProfileDetails>.Failure(OwnerVerificationErrors.NationalIdTaken);
        }

        bool wasApproved;

        try
        {
            wasApproved = profile.SaveIdentity(
                command.NationalId!,
                command.Nationality!,
                command.BirthDate.Value,
                command.IdExpiryDate.Value,
                command.Confirmed,
                DateOnly.FromDateTime(now.UtcDateTime),
                now);
        }
        catch (ArgumentException exception)
        {
            unitOfWork.DiscardTrackedChanges();

            return Result<OwnerProfileDetails>.Failure(OwnerProfileAccess.ValidationError(exception));
        }

        if (wasApproved)
        {
            await accessProvisioner.RevokeAsync(command.UserId, cancellationToken);
        }

        if (await OwnerProfileAccess.SaveAsync(unitOfWork, cancellationToken) is { } error)
        {
            return Result<OwnerProfileDetails>.Failure(error);
        }

        return Result<OwnerProfileDetails>.Success(OwnerProfileDetails.From(user, profile));
    }
}
