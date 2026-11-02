using SmartProperty.Common.Results;

namespace SmartProperty.Application.Abstractions.Ai;

/// <summary>
/// The AI search service: it holds a searchable copy of every property and answers free-text searches.
/// </summary>
/// <remarks>
/// A failure of the service — unreachable, timed out, or refusing the request — is returned as a failed
/// <see cref="Result{T}"/>, never thrown, so a caller decides what an outage means for it. Anything that is not
/// the caller's own mistake carries <see cref="AiSearchErrors.Unavailable"/>.
/// </remarks>
public interface IAiSearchGateway
{
    /// <summary>Adds the listing, or replaces it when this external id was sent before.</summary>
    Task<Result<AiListingAcknowledgement>> PutListingAsync(
        string externalId,
        AiListing listing,
        CancellationToken cancellationToken = default);

    /// <summary>Removes the listing from search. A listing that was never there counts as removed.</summary>
    Task<Result<bool>> DeleteListingAsync(string externalId, CancellationToken cancellationToken = default);

    /// <summary>Searches the listings with a free-text query, best match first.</summary>
    Task<Result<AiSearchOutcome>> SearchAsync(
        string query,
        int topK,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// One level of the place picker: countries, or the cities, areas, or compounds under a parent.
    /// </summary>
    Task<Result<IReadOnlyList<AiPlace>>> GetLocationsAsync(
        string level,
        int? parentId,
        CancellationToken cancellationToken = default);
}
