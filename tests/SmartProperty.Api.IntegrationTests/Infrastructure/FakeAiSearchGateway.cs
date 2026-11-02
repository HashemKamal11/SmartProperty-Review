using System.Collections.Concurrent;
using SmartProperty.Application.Abstractions.Ai;
using SmartProperty.Common.Results;

namespace SmartProperty.Api.IntegrationTests.Infrastructure;

/// <summary>
/// The AI search service as a test host sees it. <see cref="SmartPropertyApiFactory"/> always installs one, so no
/// test ever sends a request to the real service. Every call is recorded and every answer can be scripted.
/// </summary>
internal sealed class FakeAiSearchGateway : IAiSearchGateway
{
    public ConcurrentQueue<(string ExternalId, AiListing Listing)> Puts { get; } = new();

    public ConcurrentQueue<string> Deletes { get; } = new();

    public ConcurrentQueue<(string Query, int TopK)> Searches { get; } = new();

    public ConcurrentQueue<(string Level, int? ParentId)> LocationCalls { get; } = new();

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
        Puts.Enqueue((externalId, listing));

        return Task.FromResult(PutError is { } error
            ? Result<AiListingAcknowledgement>.Failure(error)
            : Result<AiListingAcknowledgement>.Success(new AiListingAcknowledgement(true)));
    }

    public Task<Result<bool>> DeleteListingAsync(string externalId, CancellationToken cancellationToken = default)
    {
        Deletes.Enqueue(externalId);

        return Task.FromResult(DeleteError is { } error
            ? Result<bool>.Failure(error)
            : Result<bool>.Success(true));
    }

    public Task<Result<AiSearchOutcome>> SearchAsync(
        string query,
        int topK,
        CancellationToken cancellationToken = default)
    {
        Searches.Enqueue((query, topK));

        return Task.FromResult(SearchError is { } error
            ? Result<AiSearchOutcome>.Failure(error)
            : Result<AiSearchOutcome>.Success(SearchOutcome));
    }

    public Task<Result<IReadOnlyList<AiPlace>>> GetLocationsAsync(
        string level,
        int? parentId,
        CancellationToken cancellationToken = default)
    {
        LocationCalls.Enqueue((level, parentId));

        return Task.FromResult(LocationsError is { } error
            ? Result<IReadOnlyList<AiPlace>>.Failure(error)
            : Result<IReadOnlyList<AiPlace>>.Success(Places));
    }
}
