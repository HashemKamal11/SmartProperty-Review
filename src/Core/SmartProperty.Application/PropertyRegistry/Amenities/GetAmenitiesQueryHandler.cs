using SmartProperty.Application.Abstractions.Messaging;
using SmartProperty.Application.Abstractions.Persistence.Repositories;
using SmartProperty.Common.Results;

namespace SmartProperty.Application.PropertyRegistry.Amenities;

public sealed class GetAmenitiesQueryHandler(IAmenityRepository amenityRepository)
    : IQueryHandler<GetAmenitiesQuery, IReadOnlyList<AmenityDetails>>
{
    public async Task<Result<IReadOnlyList<AmenityDetails>>> Handle(
        GetAmenitiesQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        var amenities = await amenityRepository.ListAsync(cancellationToken);

        return Result<IReadOnlyList<AmenityDetails>>.Success(amenities);
    }
}
