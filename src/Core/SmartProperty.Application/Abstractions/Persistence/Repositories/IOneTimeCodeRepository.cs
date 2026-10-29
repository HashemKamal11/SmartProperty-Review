using SmartProperty.Domain.Identity;

namespace SmartProperty.Application.Abstractions.Persistence.Repositories;

public interface IOneTimeCodeRepository
{
    /// <summary>
    /// Returns the most recently issued code for the user and purpose, whatever its state, or null when none exists.
    /// </summary>
    Task<OneTimeCode?> GetLatestAsync(
        Guid userId,
        OneTimeCodePurpose purpose,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns every code for the user and purpose that has not been consumed, so a newly issued code can retire
    /// them.
    /// </summary>
    Task<IReadOnlyList<OneTimeCode>> GetUnconsumedAsync(
        Guid userId,
        OneTimeCodePurpose purpose,
        CancellationToken cancellationToken = default);

    Task AddAsync(OneTimeCode code, CancellationToken cancellationToken = default);
}
