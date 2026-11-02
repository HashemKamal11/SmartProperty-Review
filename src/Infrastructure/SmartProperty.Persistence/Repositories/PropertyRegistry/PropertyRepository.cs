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

    /// <remarks>
    /// Two commands: one count over the filter, one page of projected rows. No entity is materialized or tracked.
    ///
    /// The ordering is newest property first, tie-broken by id descending. The tie-break is what makes paging
    /// stable — <c>created_at</c> alone is not unique, and without it a row could appear on two pages or on none.
    /// </remarks>
    public async Task<PropertyPage> ListAsync(
        PropertyType? type,
        PropertyStatus? status,
        string? countryCode,
        int skip,
        int take,
        CancellationToken cancellationToken = default)
    {
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

        if (type is { } targetType)
        {
            filtered = filtered.Where(property => property.Type == targetType);
        }

        if (status is { } targetStatus)
        {
            filtered = filtered.Where(property => property.Status == targetStatus);
        }

        if (countryCode is not null)
        {
            filtered = filtered.Where(property => property.Address.CountryCode == countryCode);
        }

        var totalCount = await filtered.LongCountAsync(cancellationToken);

        var items = await filtered
            .OrderByDescending(property => property.CreatedAt)
            .ThenByDescending(property => property.Id)
            .Skip(skip)
            .Take(take)
            .Select(PropertyDetailsProjection.Expression)
            .ToListAsync(cancellationToken);

        return new PropertyPage(items, totalCount);
    }
}
