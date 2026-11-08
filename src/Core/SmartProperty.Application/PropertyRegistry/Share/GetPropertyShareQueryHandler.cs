using SmartProperty.Application.Abstractions.Messaging;
using SmartProperty.Application.Abstractions.Persistence.Repositories;
using SmartProperty.Application.PropertyRegistry.GetById;
using SmartProperty.Common.Results;
using SmartProperty.Domain.PropertyRegistry;

namespace SmartProperty.Application.PropertyRegistry.Share;

public sealed class GetPropertyShareQueryHandler(IPropertyRepository propertyRepository)
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

        return property is { Status: PropertyStatus.Active }
            ? Result<PropertyDetails>.Success(property)
            : Result<PropertyDetails>.Failure(GetPropertyByIdErrors.NotFound);
    }
}
