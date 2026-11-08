using SmartProperty.Application.Abstractions.Persistence;
using SmartProperty.Application.Abstractions.Persistence.Repositories;
using SmartProperty.Common.Results;
using SmartProperty.Domain.Identity;
using SmartProperty.Domain.OwnerVerification;

namespace SmartProperty.Application.OwnerVerification;

/// <summary>Steps the user-facing save handlers share.</summary>
internal static class OwnerProfileAccess
{
    /// <summary>
    /// Loads the user and their tracked profile, starting an empty one when there is none. A profile under review is
    /// refused here, so no handler can edit one.
    /// </summary>
    public static async Task<Result<(User User, OwnerProfile Profile)>> LoadEditableAsync(
        Guid userId,
        IUserRepository userRepository,
        IOwnerProfileRepository profileRepository,
        Func<OwnerProfile> startProfile,
        CancellationToken cancellationToken)
    {
        if (userId == Guid.Empty)
        {
            return Result<(User, OwnerProfile)>.Failure(
                OwnerVerificationErrors.ValidationFailed("A signed-in user is required."));
        }

        var user = await userRepository.GetByIdAsync(userId, cancellationToken);

        if (user is null)
        {
            return Result<(User, OwnerProfile)>.Failure(OwnerVerificationErrors.UserNotFound);
        }

        var profile = await profileRepository.GetByUserIdAsync(userId, cancellationToken);

        if (profile is null)
        {
            profile = startProfile();
            await profileRepository.AddAsync(profile, cancellationToken);
        }
        else if (profile.IsLockedForEditing)
        {
            return Result<(User, OwnerProfile)>.Failure(OwnerVerificationErrors.LockedForReview);
        }

        return Result<(User, OwnerProfile)>.Success((user, profile));
    }

    /// <summary>
    /// Commits, turning the loss of a race for the one-profile-per-user or one-profile-per-national-ID constraint into
    /// the matching error, after dropping the abandoned changes.
    /// </summary>
    public static async Task<Error?> SaveAsync(IUnitOfWork unitOfWork, CancellationToken cancellationToken)
    {
        try
        {
            await unitOfWork.SaveChangesAsync(cancellationToken);

            return null;
        }
        catch (UniqueConstraintViolationException exception)
            when (exception.Constraint is PersistenceConstraint.OwnerProfileUser
                or PersistenceConstraint.OwnerProfileNationalId)
        {
            unitOfWork.DiscardTrackedChanges();

            return exception.Constraint == PersistenceConstraint.OwnerProfileNationalId
                ? OwnerVerificationErrors.NationalIdTaken
                : OwnerVerificationErrors.ConcurrentUpdate;
        }
    }

    /// <summary>The domain's own argument messages, without the trailing parameter name.</summary>
    public static Error ValidationError(ArgumentException exception)
    {
        var message = exception.Message;
        var marker = message.IndexOf(" (Parameter", StringComparison.Ordinal);

        return OwnerVerificationErrors.ValidationFailed(marker > 0 ? message[..marker] : message);
    }
}
