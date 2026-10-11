using SmartProperty.Application.Abstractions.Messaging;
using SmartProperty.Application.Abstractions.Persistence.Repositories;
using SmartProperty.Common.Results;

namespace SmartProperty.Application.PropertyRegistry.GetById;

/// <summary>
/// Returns one canonical property. Read-only: it saves nothing.
/// </summary>
/// <remarks>
/// An Archived property is returned unchanged; archiving retires a property, it does not hide it.
/// </remarks>
public sealed class GetPropertyByIdQueryHandler(IPropertyRepository propertyRepository)
    : IQueryHandler<GetPropertyByIdQuery, PropertyDetails>
{
    public async Task<Result<PropertyDetails>> Handle(
        GetPropertyByIdQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        // No property can have the empty id, so asking the database would only confirm what is already known.
        if (query.Id == Guid.Empty)
        {
            return Result<PropertyDetails>.Failure(GetPropertyByIdErrors.NotFound);
        }

        var property = await propertyRepository.GetDetailsByIdAsync(query.Id, cancellationToken);

        return property is null
            ? Result<PropertyDetails>.Failure(GetPropertyByIdErrors.NotFound)
            : Result<PropertyDetails>.Success(property);
    }
}
