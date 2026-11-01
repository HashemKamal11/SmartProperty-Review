using SmartProperty.Application.Abstractions.Persistence.Repositories;
using SmartProperty.Application.Marketplace.PublicListings;

namespace SmartProperty.Api.IntegrationTests.Infrastructure;

/// <summary>
/// A test-only <see cref="IPublicListingReadRepository"/> that answers exactly what a test gave it.
/// </summary>
/// <remarks>
/// It exists only in this test assembly and reaches a host only through <c>ConfigureTestServices</c>, so it can never
/// be part of the production container. A host that uses it proves how the API presents what a repository returns;
/// it says nothing about whether the real PostgreSQL query is correct.
/// </remarks>
internal sealed class StubPublicListingReadRepository : IPublicListingReadRepository
{
    /// <summary>What <see cref="SearchAsync"/> answers.</summary>
    public PublicListingPage Page { get; set; } = new([], 0);

    /// <summary>The listings <see cref="GetByIdAsync"/> can find; every other id is not found.</summary>
    public Dictionary<Guid, PublicListingDetail> Details { get; } = [];

    public int SearchCallCount { get; private set; }

    public SearchCall? LastSearchCall { get; private set; }

    public int GetByIdCallCount { get; private set; }

    public Guid? LastRequestedId { get; private set; }

    public Task<PublicListingPage> SearchAsync(
        PublicListingSearchCriteria criteria,
        int skip,
        int take,
        CancellationToken cancellationToken = default)
    {
        SearchCallCount++;
        LastSearchCall = new SearchCall(criteria, skip, take);

        return Task.FromResult(Page);
    }

    public Task<PublicListingDetail?> GetByIdAsync(Guid listingId, CancellationToken cancellationToken = default)
    {
        GetByIdCallCount++;
        LastRequestedId = listingId;

        return Task.FromResult(Details.GetValueOrDefault(listingId));
    }

    internal sealed record SearchCall(PublicListingSearchCriteria Criteria, int Skip, int Take);
}
