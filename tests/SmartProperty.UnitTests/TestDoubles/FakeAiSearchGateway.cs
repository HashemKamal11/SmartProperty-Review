using SmartProperty.Application.Abstractions.Ai;
using SmartProperty.Common.Results;

namespace SmartProperty.UnitTests.TestDoubles;

/// <summary>
/// A scripted AI search service. Every call is recorded; each answer can be replaced to script a failure.
/// </summary>
internal sealed class FakeAiSearchGateway : IAiSearchGateway
{
    public List<(string ExternalId, AiListing Listing)> Puts { get; } = [];

    public List<string> Deletes { get; } = [];

    public List<(string Query, int TopK)> Searches { get; } = [];

    public List<(string Level, int? ParentId)> LocationCalls { get; } = [];

    /// <summary>When set, every put fails with it.</summary>
    public Error? PutError { get; set; }

    public Error? DeleteError { get; set; }

    public Error? SearchError { get; set; }

    public Error? LocationsError { get; set; }

    public AiSearchOutcome SearchOutcome { get; set; } = new("q", "q", 0, []);

    public IReadOnlyList<AiPlace> Places { get; set; } = [];

    public Task<Result<AiListingAcknowledgement>> PutListingAsync(
        string externalId,
        AiListing listing,
        CancellationToken cancellationToken = default)
    {
        Puts.Add((externalId, listing));

        return Task.FromResult(PutError is { } error
            ? Result<AiListingAcknowledgement>.Failure(error)
            : Result<AiListingAcknowledgement>.Success(new AiListingAcknowledgement(true)));
    }

    public Task<Result<bool>> DeleteListingAsync(string externalId, CancellationToken cancellationToken = default)
    {
        Deletes.Add(externalId);

        return Task.FromResult(DeleteError is { } error
            ? Result<bool>.Failure(error)
            : Result<bool>.Success(true));
    }

    public Task<Result<AiSearchOutcome>> SearchAsync(
        string query,
        int topK,
        CancellationToken cancellationToken = default)
    {
        Searches.Add((query, topK));

        return Task.FromResult(SearchError is { } error
            ? Result<AiSearchOutcome>.Failure(error)
            : Result<AiSearchOutcome>.Success(SearchOutcome));
    }

    public Task<Result<IReadOnlyList<AiPlace>>> GetLocationsAsync(
        string level,
        int? parentId,
        CancellationToken cancellationToken = default)
    {
        LocationCalls.Add((level, parentId));

        return Task.FromResult(LocationsError is { } error
            ? Result<IReadOnlyList<AiPlace>>.Failure(error)
            : Result<IReadOnlyList<AiPlace>>.Success(Places));
    }
}
