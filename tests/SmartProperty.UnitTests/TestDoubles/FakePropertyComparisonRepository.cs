using SmartProperty.Application.Abstractions.Persistence.Repositories;
using SmartProperty.Application.PropertyComparison;
using SmartProperty.Application.PropertyRegistry;
using SmartProperty.Domain.PropertyComparison;

namespace SmartProperty.UnitTests.TestDoubles;

/// <summary>An in-memory comparison store that records how it was asked.</summary>
internal sealed class FakePropertyComparisonRepository : IPropertyComparisonRepository
{
    public List<PropertyComparisonItem> Stored { get; } = [];

    public List<PropertyComparisonItem> Added { get; } = [];

    /// <summary>The read model each property id is listed with.</summary>
    public Dictionary<Guid, PropertyDetails> Details { get; } = [];

    /// <summary>The percentage of the user's latest match per property id; a missing id means no match.</summary>
    public Dictionary<Guid, decimal> Matches { get; } = [];

    public Task<IReadOnlyList<PropertyComparisonItem>> ListItemsAsync(
        Guid userId,
        CancellationToken cancellationToken = default)
    {
        IReadOnlyList<PropertyComparisonItem> items = Stored
            .Where(item => item.UserId == userId)
            .OrderBy(item => item.AddedAt)
            .ThenBy(item => item.Position)
            .ToList();

        return Task.FromResult(items);
    }

    public async Task<PropertyComparisonDetails> GetAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        var items = await ListItemsAsync(userId, cancellationToken);

        var rows = items
            .Select(item => new PropertyComparisonItemDetails(
                item.Position,
                item.AddedAt,
                Details[item.PropertyId],
                Matches.TryGetValue(item.PropertyId, out var match) ? match : null,
                null))
            .ToList();

        return PropertyComparisonDetails.Of(rows, PropertyComparisonItem.MaxItems, PropertyComparisonItem.MinItemsToCompare);
    }

    public Task AddAsync(PropertyComparisonItem item, CancellationToken cancellationToken = default)
    {
        Added.Add(item);
        Stored.Add(item);

        return Task.CompletedTask;
    }

    public Task<bool> RemoveAsync(Guid userId, Guid propertyId, CancellationToken cancellationToken = default)
    {
        var removed = Stored.RemoveAll(item => item.UserId == userId && item.PropertyId == propertyId) > 0;

        return Task.FromResult(removed);
    }

    public Task ClearAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        Stored.RemoveAll(item => item.UserId == userId);

        return Task.CompletedTask;
    }
}
