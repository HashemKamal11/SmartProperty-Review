using SmartProperty.Application.Abstractions.Messaging;
using SmartProperty.Application.Abstractions.Persistence;
using SmartProperty.Application.Abstractions.Persistence.Repositories;
using SmartProperty.Application.Abstractions.Time;
using SmartProperty.Common.Results;

namespace SmartProperty.Application.OwnerVerification.Submit;

/// <summary>Sends the signed-in user's finished owner profile to the reviewers.</summary>
public sealed record SubmitOwnerProfileCommand(Guid UserId) : ICommand<OwnerProfileDetails>;

public sealed class SubmitOwnerProfileCommandHandler(
    IUserRepository userRepository,
    IOwnerProfileRepository profileRepository,
    IDateTimeProvider dateTimeProvider,
    IUnitOfWork unitOfWork)
    : ICommandHandler<SubmitOwnerProfileCommand, OwnerProfileDetails>
{
    public async Task<Result<OwnerProfileDetails>> Handle(
        SubmitOwnerProfileCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (command.UserId == Guid.Empty)
        {
            return Failure(OwnerVerificationErrors.ValidationFailed("A signed-in user is required."));
        }

        var user = await userRepository.GetByIdAsync(command.UserId, cancellationToken);

        if (user is null)
        {
            return Failure(OwnerVerificationErrors.UserNotFound);
        }

        var profile = await profileRepository.GetByUserIdAsync(command.UserId, cancellationToken);

        if (profile is null || !profile.IsIdentityComplete)
        {
            return Failure(OwnerVerificationErrors.IdentityIncomplete);
        }

        if (profile.IsLockedForEditing)
        {
            return Failure(OwnerVerificationErrors.LockedForReview);
        }

        if (!profile.IsAddressComplete)
        {
            return Failure(OwnerVerificationErrors.AddressIncomplete);
        }

        // Approved profiles have nothing to submit: an edit would already have returned them to Draft.
        if (profile.Status == SmartProperty.Domain.OwnerVerification.OwnerVerificationStatus.Approved)
        {
            return Result<OwnerProfileDetails>.Success(OwnerProfileDetails.From(user, profile));
        }

        profile.Submit(dateTimeProvider.UtcNow);

        await unitOfWork.SaveChangesAsync(cancellationToken);

        return Result<OwnerProfileDetails>.Success(OwnerProfileDetails.From(user, profile));
    }

    private static Result<OwnerProfileDetails> Failure(Error error)
    {
        return Result<OwnerProfileDetails>.Failure(error);
    }
}
