using SmartProperty.Application.Abstractions.Messaging;
using SmartProperty.Application.Abstractions.Persistence;
using SmartProperty.Application.Abstractions.Persistence.Repositories;
using SmartProperty.Application.Abstractions.Time;
using SmartProperty.Common.Results;
using SmartProperty.Domain.OwnerVerification;

namespace SmartProperty.Application.OwnerVerification.Save;

/// <summary>Saves the national-address step (step 3) of the signed-in user's owner profile, without the proof image.</summary>
public sealed record SaveOwnerAddressCommand(
    Guid UserId,
    string? BuildingNumber,
    string? StreetName,
    string? District,
    string? City,
    string? PostalCode,
    string? AdditionalNumber,
    bool Confirmed) : ICommand<OwnerProfileDetails>;

public sealed class SaveOwnerAddressCommandHandler(
    IUserRepository userRepository,
    IOwnerProfileRepository profileRepository,
    OwnerAccessProvisioner accessProvisioner,
    IDateTimeProvider dateTimeProvider,
    IUnitOfWork unitOfWork)
    : ICommandHandler<SaveOwnerAddressCommand, OwnerProfileDetails>
{
    public async Task<Result<OwnerProfileDetails>> Handle(
        SaveOwnerAddressCommand command,
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
        bool wasApproved;

        try
        {
            wasApproved = profile.SaveAddress(
                command.BuildingNumber!,
                command.StreetName!,
                command.District!,
                command.City!,
                command.PostalCode!,
                command.AdditionalNumber!,
                command.Confirmed,
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
