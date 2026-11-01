using SmartProperty.Application.Abstractions.Persistence.Repositories;
using SmartProperty.Application.Marketplace.PublicListings;

namespace SmartProperty.UnitTests.TestDoubles;

internal sealed class FakePublicListingReadRepository : IPublicListingReadRepository
{
    /// <summary>What <see cref="SearchAsync"/> answers, returned as-is so a test can prove nothing is added or removed.</summary>
    public PublicListingPage Page { get; set; } = new([], 0);

    /// <summary>The listings <see cref="GetByIdAsync"/> can find; every other id is not found.</summary>
    public Dictionary<Guid, PublicListingDetail> Details { get; } = [];

    /// <summary>When set, the repository throws it instead of answering.</summary>
    public Exception? ExceptionToThrow { get; set; }

    public int SearchCallCount { get; private set; }

    public SearchCall? LastSearchCall { get; private set; }

    public int GetByIdCallCount { get; private set; }

    public Guid? LastRequestedId { get; private set; }

    public CancellationToken CancellationToken { get; private set; }

    public Task<PublicListingPage> SearchAsync(
        PublicListingSearchCriteria criteria,
        int skip,
        int take,
        CancellationToken cancellationToken = default)
    {
        SearchCallCount++;
        LastSearchCall = new SearchCall(criteria, skip, take);
        CancellationToken = cancellationToken;

        if (ExceptionToThrow is not null)
        {
            throw ExceptionToThrow;
        }

        return Task.FromResult(Page);
    }

    public Task<PublicListingDetail?> GetByIdAsync(Guid listingId, CancellationToken cancellationToken = default)
    {
        GetByIdCallCount++;
        LastRequestedId = listingId;
        CancellationToken = cancellationToken;

        if (ExceptionToThrow is not null)
        {
            throw ExceptionToThrow;
        }

        return Task.FromResult(Details.GetValueOrDefault(listingId));
    }

    internal sealed record SearchCall(PublicListingSearchCriteria Criteria, int Skip, int Take);
}
