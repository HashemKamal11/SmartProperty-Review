#nullable enable
using SmartProperty.Application.Abstractions.Ai;
using SmartProperty.Application.Abstractions.Persistence.Repositories;
using SmartProperty.Application.Marketplace.PublicListings;
using SmartProperty.Domain.Marketplace;

namespace SmartProperty.Api.Infrastructure.AiSearch;

/// <summary>
/// Keeps the AI search service's advertisements in step with the marketplace, best effort and after the commit.
/// </summary>
/// <remarks>
/// Never throws and never changes what was committed. A failure, a disabled service, or a property the service cannot
/// take (it requires an area, an area in square metres and room counts) is logged with a fixed category and the ids
/// involved, and nothing else: no title, description, price or address. Nothing retries; see
/// <see cref="IListingSearchIndexer"/>.
/// </remarks>
internal sealed class AiListingIndexer(
    IAiSearchGateway gateway,
    IPropertyRepository properties,
    IPropertyListingsLookup listings,
    IPublicListingReadRepository publicListings,
    ILogger<AiListingIndexer> logger) : IListingSearchIndexer
{
    public async Task IndexAsync(Guid propertyId, ListingPurpose purpose, CancellationToken cancellationToken = default)
    {
        try
        {
            var current = await listings.GetCurrentAsync([propertyId], cancellationToken);
            var summary = current.TryGetValue(propertyId, out var visible) ? visible.FirstOrDefault(listing => listing.Purpose == purpose) : null;

            if (summary is null)
            {
                logger.LogInformation("AI index skipped: property {PropertyId} has no visible {Purpose} listing.", propertyId, purpose);

                return;
            }

            var detail = await publicListings.GetByIdAsync(summary.ListingId, cancellationToken);
            var property = await properties.GetByIdAsync(propertyId, cancellationToken);

            if (detail is null || property is null)
            {
                return;
            }

            var blockers = ListingAiMapper.Blockers(property, detail);

            if (blockers.Count > 0)
            {
                logger.LogWarning(
                    "AI index skipped for listing {ListingId}: {Blockers}.", summary.ListingId, string.Join("; ", blockers));

                return;
            }

            var result = await gateway.PutListingAsync(
                summary.ListingId.ToString("D"), ListingAiMapper.ToListing(property, detail), cancellationToken);

            if (result.IsFailure)
            {
                logger.LogWarning("AI index failed for listing {ListingId}: {Code}.", summary.ListingId, result.Error!.Code);
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning("AI index failed for property {PropertyId}: {ExceptionType}.", propertyId, exception.GetType().Name);
        }
    }

    public async Task RemoveAsync(Guid listingId, CancellationToken cancellationToken = default)
    {
        try
        {
            var result = await gateway.DeleteListingAsync(listingId.ToString("D"), cancellationToken);

            if (result.IsFailure)
            {
                logger.LogWarning("AI removal failed for listing {ListingId}: {Code}.", listingId, result.Error!.Code);
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning("AI removal failed for listing {ListingId}: {ExceptionType}.", listingId, exception.GetType().Name);
        }
    }
}
