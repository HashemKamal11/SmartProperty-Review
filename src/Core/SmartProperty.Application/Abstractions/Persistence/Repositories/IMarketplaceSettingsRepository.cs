using SmartProperty.Domain.Marketplace;

namespace SmartProperty.Application.Abstractions.Persistence.Repositories;

/// <summary>The administrator-controlled marketplace settings.</summary>
public interface IMarketplaceSettingsRepository
{
    /// <summary>The configured signing window in hours, or the default when an administrator has never set one.</summary>
    Task<int> GetSigningWindowHoursAsync(CancellationToken cancellationToken = default);

    /// <summary>The stored setting, or null when none has been stored.</summary>
    Task<MarketplaceSettings?> GetAsync(CancellationToken cancellationToken = default);

    /// <summary>Stores the setting, creating it the first time. The last writer wins.</summary>
    Task SaveSigningWindowAsync(int signingWindowHours, Guid administratorUserId, DateTimeOffset at, CancellationToken cancellationToken = default);
}
