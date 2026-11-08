using Microsoft.EntityFrameworkCore;
using SmartProperty.Application.Abstractions.Persistence.Repositories;
using SmartProperty.Application.PropertyComparison;
using SmartProperty.Domain.PropertyComparison;
using SmartProperty.Persistence.Context;
using SmartProperty.Persistence.Repositories.PropertyRegistry;

namespace SmartProperty.Persistence.Repositories.PropertyComparison;

internal sealed class PropertyComparisonRepository(ApplicationDbContext dbContext) : IPropertyComparisonRepository
{
    public async Task<IReadOnlyList<PropertyComparisonItem>> ListItemsAsync(
        Guid userId,
        CancellationToken cancellationToken = default)
    {
        return await dbContext.PropertyComparisonItems
            .AsNoTracking()
            .Where(item => item.UserId == userId)
            .OrderBy(item => item.AddedAt)
            .ThenBy(item => item.Position)
            .ToListAsync(cancellationToken);
    }

    /// <remarks>
    /// Three commands at most: the user's items, the read models of just those properties projected in the database by
    /// the same expression the property endpoints use, and the user's matches for them. No entity is tracked. Items are
    /// returned in the order they were added, tie-broken by position.
    /// </remarks>
    public async Task<PropertyComparisonDetails> GetAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        var items = await ListItemsAsync(userId, cancellationToken);

        if (items.Count == 0)
        {
            return PropertyComparisonDetails.Of([], PropertyComparisonItem.MaxItems, PropertyComparisonItem.MinItemsToCompare);
        }

        var ids = items.Select(item => item.PropertyId).ToList();

        var details = await dbContext.Properties
            .AsNoTracking()
            .Where(property => ids.Contains(property.Id))
            .Select(PropertyDetailsProjection.Expression)
            .ToListAsync(cancellationToken);
        var detailsById = details.ToDictionary(detail => detail.Id);

        // The user's matches for these properties, newest first, so the first of each property is the latest.
        var matches = await dbContext.PropertyMatches
            .AsNoTracking()
            .Where(match => match.UserId == userId && ids.Contains(match.PropertyId))
            .OrderByDescending(match => match.CreatedAt)
            .ThenByDescending(match => match.Id)
            .Select(match => new { match.PropertyId, match.MatchPercentage, match.Description })
            .ToListAsync(cancellationToken);
        var latestMatch = matches
            .GroupBy(match => match.PropertyId)
            .ToDictionary(group => group.Key, group => group.First());

        // The foreign key guarantees every item has its property.
        var rows = items
            .Where(item => detailsById.ContainsKey(item.PropertyId))
            .Select(item =>
            {
                latestMatch.TryGetValue(item.PropertyId, out var match);

                return new PropertyComparisonItemDetails(
                    item.Position,
                    item.AddedAt,
                    detailsById[item.PropertyId],
                    match?.MatchPercentage,
                    match?.Description);
            })
            .ToList();

        return PropertyComparisonDetails.Of(rows, PropertyComparisonItem.MaxItems, PropertyComparisonItem.MinItemsToCompare);
    }

    public async Task AddAsync(PropertyComparisonItem item, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(item);
        await dbContext.PropertyComparisonItems.AddAsync(item, cancellationToken);
    }

    public async Task<bool> RemoveAsync(Guid userId, Guid propertyId, CancellationToken cancellationToken = default)
    {
        var tracked = await dbContext.PropertyComparisonItems.FirstOrDefaultAsync(
            item => item.UserId == userId && item.PropertyId == propertyId,
            cancellationToken);

        if (tracked is null)
        {
            return false;
        }

        dbContext.PropertyComparisonItems.Remove(tracked);

        return true;
    }

    public async Task ClearAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        var tracked = await dbContext.PropertyComparisonItems
            .Where(item => item.UserId == userId)
            .ToListAsync(cancellationToken);

        dbContext.PropertyComparisonItems.RemoveRange(tracked);
    }
}
