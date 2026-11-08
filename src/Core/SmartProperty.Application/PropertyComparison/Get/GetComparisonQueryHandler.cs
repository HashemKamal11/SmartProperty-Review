using SmartProperty.Application.Abstractions.Messaging;
using SmartProperty.Application.Abstractions.Persistence.Repositories;
using SmartProperty.Common.Results;

namespace SmartProperty.Application.PropertyComparison.Get;

/// <summary>The signed-in user's comparison list, with everything the comparison screen shows.</summary>
public sealed record GetComparisonQuery(Guid UserId) : IQuery<PropertyComparisonDetails>;

public sealed class GetComparisonQueryHandler(IPropertyComparisonRepository comparisonRepository)
    : IQueryHandler<GetComparisonQuery, PropertyComparisonDetails>
{
    public async Task<Result<PropertyComparisonDetails>> Handle(
        GetComparisonQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        if (query.UserId == Guid.Empty)
        {
            return Result<PropertyComparisonDetails>.Failure(
                PropertyComparisonErrors.ValidationFailed("A signed-in user is required."));
        }

        return Result<PropertyComparisonDetails>.Success(
            await comparisonRepository.GetAsync(query.UserId, cancellationToken));
    }
}
