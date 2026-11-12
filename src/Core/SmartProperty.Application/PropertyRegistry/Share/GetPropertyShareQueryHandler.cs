using SmartProperty.Application.Abstractions.Messaging;
using SmartProperty.Application.Abstractions.Persistence.Repositories;
using SmartProperty.Application.PropertyRegistry.GetById;
using SmartProperty.Common.Results;
using SmartProperty.Domain.PropertyRegistry;

namespace SmartProperty.Application.PropertyRegistry.Share;

public sealed class GetPropertyShareQueryHandler(
    IPropertyRepository propertyRepository,
    IPropertyListingsLookup? listings = null)
    : IQueryHandler<GetPropertyShareQuery, PropertyDetails>
{
    public async Task<Result<PropertyDetails>> Handle(
        GetPropertyShareQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        if (query.Id == Guid.Empty)
        {
            return Result<PropertyDetails>.Failure(GetPropertyByIdErrors.NotFound);
        }

        var property = await propertyRepository.GetDetailsByIdAsync(query.Id, cancellationToken);

        if (property is not { Status: PropertyStatus.Active })
        {
            return Result<PropertyDetails>.Failure(GetPropertyByIdErrors.NotFound);
        }

        // The public sees a property only through a published listing, so only an advertised property can be shared.
        if (listings is not null)
        {
            var current = await listings.GetCurrentAsync([property.Id], cancellationToken);

            if (!current.TryGetValue(property.Id, out var visible) || visible.Count == 0)
            {
                return Result<PropertyDetails>.Failure(GetPropertyByIdErrors.NotFound);
            }
        }

        return Result<PropertyDetails>.Success(property);
    }
}
