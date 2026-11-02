using Microsoft.EntityFrameworkCore;
using SmartProperty.Application.Abstractions.Persistence.Repositories;
using SmartProperty.Application.PropertyRegistry;
using SmartProperty.Domain.PropertyRegistry;
using SmartProperty.Persistence.Context;

namespace SmartProperty.Persistence.Repositories.PropertyRegistry;

internal sealed class ViewRepository(ApplicationDbContext dbContext) : IViewRepository
{
    /// <remarks>Tracked, so the property being created or updated can reference the very same instances.</remarks>
    public async Task<IReadOnlyList<PropertyView>> GetByCodesAsync(
        IReadOnlyCollection<string> codes,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(codes);

        return await dbContext.PropertyViews
            .Where(view => codes.Contains(view.Code))
            .OrderBy(view => view.SortOrder)
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<ViewDetails>> ListAsync(CancellationToken cancellationToken = default)
    {
        return await dbContext.PropertyViews
            .AsNoTracking()
            .OrderBy(view => view.SortOrder)
            .Select(view => new ViewDetails(view.Code, view.NameAr, view.NameEn))
            .ToListAsync(cancellationToken);
    }
}
