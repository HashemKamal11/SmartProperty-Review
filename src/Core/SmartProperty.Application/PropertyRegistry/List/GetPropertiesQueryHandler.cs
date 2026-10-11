using SmartProperty.Application.Abstractions.Messaging;
using SmartProperty.Application.Abstractions.Persistence.Repositories;
using SmartProperty.Common.Pagination;
using SmartProperty.Common.Results;

namespace SmartProperty.Application.PropertyRegistry.List;

/// <summary>
/// Returns one page of the canonical property registry, newest first. Read-only: it saves nothing.
/// </summary>
/// <remarks>
/// There is no implicit status filter: Archived properties are listed alongside Active ones unless the caller asks
/// for one status. Defaulting and clamping happen here rather than at the API boundary, so every caller of the use
/// case gets the same bounded page regardless of what it passed.
/// </remarks>
public sealed class GetPropertiesQueryHandler(IPropertyRepository propertyRepository)
    : IQueryHandler<GetPropertiesQuery, PagedList<PropertyDetails>>
{
    public async Task<Result<PagedList<PropertyDetails>>> Handle(
        GetPropertiesQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        if (GetPropertiesQueryValidator.Validate(query, out var countryCode) is { } validationError)
        {
            return Result<PagedList<PropertyDetails>>.Failure(validationError);
        }

        // PageParameters owns both bounds and checked offset arithmetic, so no caller-controlled value can wrap
        // into a negative or multi-billion-row database offset.
        var pageParameters = new PageParameters();

        if (query.Page is { } page)
        {
            pageParameters.PageNumber = page;
        }

        if (query.PageSize is { } pageSize)
        {
            pageParameters.PageSize = pageSize;
        }

        var result = await propertyRepository.ListAsync(
            query.Type,
            query.Status,
            countryCode,
            pageParameters.Offset,
            pageParameters.PageSize,
            cancellationToken);

        return Result<PagedList<PropertyDetails>>.Success(
            PagedList<PropertyDetails>.Create(
                result.Items,
                pageParameters.PageNumber,
                pageParameters.PageSize,
                result.TotalCount));
    }
}
