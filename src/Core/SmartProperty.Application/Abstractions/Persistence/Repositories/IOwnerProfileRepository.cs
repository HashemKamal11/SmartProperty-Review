using SmartProperty.Application.OwnerVerification;
using SmartProperty.Domain.OwnerVerification;

namespace SmartProperty.Application.Abstractions.Persistence.Repositories;

public interface IOwnerProfileRepository
{
    /// <summary>The user's profile, tracked so a change to it is saved, or null when they have not started one.</summary>
    Task<OwnerProfile?> GetByUserIdAsync(Guid userId, CancellationToken cancellationToken = default);

    /// <summary>The profile with this id, tracked, or null.</summary>
    Task<OwnerProfile?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>Tracks a new profile. Committed by the caller.</summary>
    Task AddAsync(OwnerProfile profile, CancellationToken cancellationToken = default);

    /// <summary>True when a profile of a different user already carries this national ID from this country.</summary>
    Task<bool> NationalIdUsedByAnotherAsync(
        string idCountryCode,
        string nationalId,
        Guid userId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// One page of profiles that have left Draft, with the account data of each owner. Profiles still being filled in
    /// are never listed. Pending ones are oldest first; every other order is newest first.
    /// </summary>
    Task<OwnerReviewPage> ListForReviewAsync(
        OwnerVerificationStatus? status,
        int skip,
        int take,
        CancellationToken cancellationToken = default);
}
