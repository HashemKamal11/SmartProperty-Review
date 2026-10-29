using SmartProperty.Domain.Identity;

namespace SmartProperty.Application.Abstractions.Persistence.Repositories;

public interface IPhoneVerificationRepository
{
    /// <summary>
    /// Returns the most recently issued verification for the user, whatever its state, or null when none exists.
    /// </summary>
    Task<PhoneVerification?> GetLatestByUserIdAsync(Guid userId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns every verification for the user that has not been consumed, so a newly issued code can retire them.
    /// </summary>
    Task<IReadOnlyList<PhoneVerification>> GetUnconsumedByUserIdAsync(
        Guid userId,
        CancellationToken cancellationToken = default);

    Task AddAsync(PhoneVerification verification, CancellationToken cancellationToken = default);
}
