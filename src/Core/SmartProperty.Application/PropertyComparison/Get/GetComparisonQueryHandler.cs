using SmartProperty.Application.Abstractions.Messaging;
using SmartProperty.Application.Abstractions.Persistence.Repositories;
using SmartProperty.Common.Results;

namespace SmartProperty.Application.PropertyComparison.Get;

/// <summary>The signed-in user's comparison list, with everything the comparison screen shows.</summary>
public sealed record GetComparisonQuery(Guid UserId) : IQuery<PropertyComparisonDetails>;

public sealed class GetComparisonQueryHandler(
    IPropertyComparisonRepository comparisonRepository,
    IPropertyListingsLookup? listings = null)
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

        var comparison = await comparisonRepository.GetAsync(query.UserId, cancellationToken);

        // Prices and terms live on listings, so each compared property carries the advertisements currently visible
        // for it. CanCompare and the counts are unchanged: they depend on how many properties are compared.
        if (listings is not null && comparison.Items.Count > 0)
        {
            var current = await listings.GetCurrentAsync(comparison.Items.Select(item => item.Property.Id).ToArray(), cancellationToken);
            comparison = comparison with
            {
                Items = comparison.Items
                    .Select(item => item with { Listings = current.TryGetValue(item.Property.Id, out var found) ? found : [] })
                    .ToArray()
            };
        }

        return Result<PropertyComparisonDetails>.Success(comparison);
    }
}
