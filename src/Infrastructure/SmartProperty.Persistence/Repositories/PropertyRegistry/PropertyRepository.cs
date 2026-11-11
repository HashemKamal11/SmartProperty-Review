using Microsoft.EntityFrameworkCore;
using SmartProperty.Application.Abstractions.Persistence.Repositories;
using SmartProperty.Application.Locations;
using SmartProperty.Application.PropertyRegistry;
using SmartProperty.Application.PropertyRegistry.List;
using SmartProperty.Common.Pagination;
using SmartProperty.Domain.PropertyRegistry;
using SmartProperty.Persistence.Context;

namespace SmartProperty.Persistence.Repositories.PropertyRegistry;

internal sealed class PropertyRepository(ApplicationDbContext dbContext) : IPropertyRepository
{
    public async Task AddAsync(Property property, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(property);
        await dbContext.Properties.AddAsync(property, cancellationToken);
    }

    /// <remarks>
    /// One command, tracked. The caller mutates the returned aggregate and change tracking writes only what
    /// changed; nothing here calls <c>Update</c> or <c>Attach</c>.
    /// </remarks>
    public Task<Property?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        return dbContext.Properties
            .Include(property => property.Amenities)
            .Include(property => property.Views)
            .Include(property => property.Images)
            .Include(property => property.Area!)
                .ThenInclude(area => area.City)
                .ThenInclude(city => city.Country)
            .Include(property => property.Compound)
            .FirstOrDefaultAsync(property => property.Id == id, cancellationToken);
    }

    public async Task<IReadOnlyCollection<Guid>> GetExistingIdsAsync(
        IReadOnlyCollection<Guid> ids,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(ids);

        return await dbContext.Properties
            .AsNoTracking()
            .Where(property => ids.Contains(property.Id))
            .Select(property => property.Id)
            .ToListAsync(cancellationToken);
    }

    /// <remarks>
    /// One command. The read model is projected in the database, so no aggregate is materialized or tracked.
    /// </remarks>
    public Task<PropertyDetails?> GetDetailsByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        return dbContext.Properties
            .AsNoTracking()
            .Where(property => property.Id == id)
            .Select(PropertyDetailsProjection.Expression)
            .SingleOrDefaultAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<PropertyDetails>> GetDetailsByIdsAsync(
        IReadOnlyCollection<Guid> ids,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(ids);

        if (ids.Count == 0)
        {
            return [];
        }

        var wanted = ids.ToList();

        return await dbContext.Properties
            .AsNoTracking()
            .Where(property => wanted.Contains(property.Id))
            .Select(PropertyDetailsProjection.Expression)
            .ToListAsync(cancellationToken);
    }

    public Task<PropertyPage> ListAsync(
        PropertyType? type,
        PropertyStatus? status,
        string? countryCode,
        int skip,
        int take,
        CancellationToken cancellationToken = default)
    {
        return SearchAsync(
            new PropertyListFilter(type is { } single ? [single] : null, status, countryCode),
            skip,
            take,
            cancellationToken);
    }

    /// <remarks>
    /// Two commands, plus one more for the page's match percentages when a match user is named. No entity is
    /// materialized or tracked. Every non-null filter must hold; the types are an "any of" set.
    ///
    /// The ordering is newest property first, tie-broken by id descending. The tie-break is what makes paging
    /// stable — <c>created_at</c> alone is not unique, and without it a row could appear on two pages or on none.
    /// </remarks>
    public async Task<PropertyPage> SearchAsync(
        PropertyListFilter filter,
        int skip,
        int take,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(filter);

        if (skip < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(skip), skip, "Skip must not be negative.");
        }

        if (skip > PageParameters.MaxOffset)
        {
            throw new ArgumentOutOfRangeException(
                nameof(skip),
                skip,
                $"Skip must not exceed {PageParameters.MaxOffset}.");
        }

        if (take is < 1 or > PageParameters.MaxPageSize)
        {
            throw new ArgumentOutOfRangeException(
                nameof(take),
                take,
                $"Take must be between 1 and {PageParameters.MaxPageSize}.");
        }

        var filtered = dbContext.Properties.AsNoTracking();

        if (filter.Types is { Count: > 0 } types)
        {
            var wantedTypes = types.ToArray();
            filtered = filtered.Where(property => wantedTypes.Contains(property.Type));
        }

        if (filter.Status is { } targetStatus)
        {
            filtered = filtered.Where(property => property.Status == targetStatus);
        }

        if (filter.CountryCode is { } countryCode)
        {
            filtered = filtered.Where(property => property.Address.CountryCode == countryCode);
        }

        foreach (var term in filter.SearchTerms ?? [])
        {
            var pattern = LikePattern(term);
            filtered = filtered.Where(property =>
                EF.Functions.ILike(property.Address.City ?? "", pattern, LikeEscape)
                || EF.Functions.ILike(property.Address.Region ?? "", pattern, LikeEscape)
                || EF.Functions.ILike(property.Address.District ?? "", pattern, LikeEscape)
                || EF.Functions.ILike(property.Address.AddressLine ?? "", pattern, LikeEscape)
                || (property.Area != null
                    && (EF.Functions.ILike(property.Area.NameEn, pattern, LikeEscape)
                        || EF.Functions.ILike(property.Area.NameAr, pattern, LikeEscape))));
        }

        if (filter.District is { } district)
        {
            var lowered = district.ToLowerInvariant();
            filtered = filtered.Where(property => property.Address.District != null
                && property.Address.District.ToLower() == lowered);
        }

        if (filter.AreaId is { } areaId)
        {
            filtered = filtered.Where(property => property.AreaId == areaId);
        }

        if (filter.MinBedrooms is { } minBedrooms)
        {
            filtered = filtered.Where(property => property.Bedrooms >= minBedrooms);
        }

        if (filter.MaxBedrooms is { } maxBedrooms)
        {
            filtered = filtered.Where(property => property.Bedrooms <= maxBedrooms);
        }

        if (filter is { MinMatchPercentage: { } minMatch, MatchUserId: { } matchUserId })
        {
            // The user's latest stored match for the property decides; an older, higher score does not count.
            filtered = filtered.Where(property => dbContext.PropertyMatches
                .Where(match => match.UserId == matchUserId && match.PropertyId == property.Id)
                .OrderByDescending(match => match.CreatedAt)
                .Select(match => (decimal?)match.MatchPercentage)
                .FirstOrDefault() >= minMatch);
        }

        var totalCount = await filtered.LongCountAsync(cancellationToken);

        var items = await Order(filtered, filter)
            .Skip(skip)
            .Take(take)
            .Select(PropertyDetailsProjection.Expression)
            .ToListAsync(cancellationToken);

        if (filter.MatchUserId is { } userId && items.Count > 0)
        {
            var ids = items.Select(item => item.Id).ToArray();
            var matches = await dbContext.PropertyMatches
                .AsNoTracking()
                .Where(match => match.UserId == userId && ids.Contains(match.PropertyId))
                .Select(match => new { match.PropertyId, match.MatchPercentage, match.CreatedAt })
                .ToListAsync(cancellationToken);

            var latest = matches
                .GroupBy(match => match.PropertyId)
                .ToDictionary(
                    group => group.Key,
                    group => group.OrderByDescending(match => match.CreatedAt).First().MatchPercentage);

            items = items
                .Select(item => latest.TryGetValue(item.Id, out var percentage)
                    ? item with { MatchPercentage = percentage }
                    : item)
                .ToList();
        }

        return new PropertyPage(items, totalCount);
    }

    /// <summary>
    /// Every order ends newest first and then id descending, so paging is stable. Relevance ranks by the match
    /// user's latest stored percentage (no match last), then by where the whole search phrase appears — location,
    /// then address line — and degrades to newest first when neither signal exists.
    /// </summary>
    private IOrderedQueryable<Property> Order(IQueryable<Property> filtered, PropertyListFilter filter)
    {
        IOrderedQueryable<Property> ordered;
        var createdAtOrdered = false;

        switch (filter.Sort)
        {
            case PropertyListSortOrder.Relevance when filter.MatchUserId is { } userId:
                ordered = filtered.OrderByDescending(property => dbContext.PropertyMatches
                    .Where(match => match.UserId == userId && match.PropertyId == property.Id)
                    .OrderByDescending(match => match.CreatedAt)
                    .Select(match => (decimal?)match.MatchPercentage)
                    .FirstOrDefault() ?? -1m);
                if (filter.SearchTerms is { Count: > 0 } matchTerms)
                {
                    ordered = ordered.ThenByDescending(TextRank(LikePattern(string.Join(' ', matchTerms))));
                }

                break;

            case PropertyListSortOrder.Relevance when filter.SearchTerms is { Count: > 0 } terms:
                ordered = filtered.OrderByDescending(TextRank(LikePattern(string.Join(' ', terms))));
                break;

            default:
                ordered = filtered.OrderByDescending(property => property.CreatedAt);
                createdAtOrdered = true;
                break;
        }

        if (!createdAtOrdered)
        {
            ordered = ordered.ThenByDescending(property => property.CreatedAt);
        }

        return ordered.ThenByDescending(property => property.Id);
    }

    private static System.Linq.Expressions.Expression<Func<Property, int>> TextRank(string phrase)
    {
        // Location first, then the street address line.
        return property =>
            EF.Functions.ILike(property.Address.District ?? "", phrase, LikeEscape)
            || EF.Functions.ILike(property.Address.City ?? "", phrase, LikeEscape)
            || EF.Functions.ILike(property.Address.Region ?? "", phrase, LikeEscape)
                ? 2
                : EF.Functions.ILike(property.Address.AddressLine ?? "", phrase, LikeEscape)
                    ? 1
                    : 0;
    }

    private const string LikeEscape = "\\";

    /// <summary>A contains-pattern for PostgreSQL <c>ILIKE</c> with the user's wildcards neutralized.</summary>
    private static string LikePattern(string text)
    {
        var escaped = text.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");

        return $"%{escaped}%";
    }
}
