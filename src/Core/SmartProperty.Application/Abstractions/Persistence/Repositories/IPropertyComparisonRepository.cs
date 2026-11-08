using SmartProperty.Application.PropertyComparison;
using SmartProperty.Domain.PropertyComparison;

namespace SmartProperty.Application.Abstractions.Persistence.Repositories;

public interface IPropertyComparisonRepository
{
    /// <summary>The user's list as stored, in position order, with no property data. Not tracked.</summary>
    Task<IReadOnlyList<PropertyComparisonItem>> ListItemsAsync(
        Guid userId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// The user's list as the comparison screen shows it: each property in full, in the order it was added, with the
    /// user's latest AI match for it where there is one. Archived properties stay on the list and are shown.
    /// </summary>
    Task<PropertyComparisonDetails> GetAsync(Guid userId, CancellationToken cancellationToken = default);

    /// <summary>Tracks a new item. Committed by the caller.</summary>
    Task AddAsync(PropertyComparisonItem item, CancellationToken cancellationToken = default);

    /// <summary>Tracks the removal of the property from the user's list and reports whether it was on it. Committed by the caller.</summary>
    Task<bool> RemoveAsync(Guid userId, Guid propertyId, CancellationToken cancellationToken = default);

    /// <summary>Tracks the removal of every item of the user's list. Committed by the caller.</summary>
    Task ClearAsync(Guid userId, CancellationToken cancellationToken = default);
}
