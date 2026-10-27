using SmartProperty.Application.Abstractions.Persistence.Repositories;
using SmartProperty.Application.Marketplace.Authority;
using SmartProperty.Domain.Marketplace;

namespace SmartProperty.UnitTests.Application.Marketplace.ListingSubmissions.Write;

/// <summary>
/// An in-memory stand-in for the write repository that records the order of the calls it receives. Only the two reads
/// the write gate makes are supported; every other operation fails the test that reaches it.
/// </summary>
internal sealed class FakeListingSubmissionWriteRepository : IListingSubmissionWriteRepository
{
    /// <summary>The calls in the order they were made.</summary>
    public List<string> Calls { get; } = [];

    public LockedPropertyView? Property { get; set; }

    public AuthorityGrantView? Grant { get; set; }

    public Exception? ExceptionToThrow { get; set; }

    public Guid? LastGrantee { get; private set; }

    public Guid? LastGrantPropertyId { get; private set; }

    public ListingPurpose? LastGrantPurpose { get; private set; }

    public CancellationToken CancellationToken { get; private set; }

    public Task<LockedPropertyView?> LockPropertyAsync(Guid propertyId, CancellationToken cancellationToken = default)
    {
        Calls.Add("LockProperty");
        CancellationToken = cancellationToken;

        if (ExceptionToThrow is not null)
        {
            throw ExceptionToThrow;
        }

        return Task.FromResult(Property);
    }

    public Task<AuthorityGrantView?> GetGrantForSharedLockAsync(
        Guid granteeUserId,
        Guid propertyId,
        ListingPurpose purpose,
        CancellationToken cancellationToken = default)
    {
        Calls.Add("GetGrant");
        LastGrantee = granteeUserId;
        LastGrantPropertyId = propertyId;
        LastGrantPurpose = purpose;

        return Task.FromResult(Grant);
    }

    public Task<PropertyListing?> GetListingAsync(Guid propertyId, ListingPurpose purpose, CancellationToken cancellationToken = default)
    {
        throw new NotSupportedException();
    }

    public Task AddListingAsync(PropertyListing listing, CancellationToken cancellationToken = default)
    {
        throw new NotSupportedException();
    }

    public Task<ListingSubmission?> GetLatestSubmissionAsync(Guid listingId, CancellationToken cancellationToken = default)
    {
        throw new NotSupportedException();
    }

    public Task<ListingSubmission?> GetOwnSubmissionAsync(Guid submissionId, Guid actingUserId, CancellationToken cancellationToken = default)
    {
        throw new NotSupportedException();
    }

    public Task AddSubmissionAsync(ListingSubmission submission, CancellationToken cancellationToken = default)
    {
        throw new NotSupportedException();
    }

    public Task RemoveDraftAsync(ListingSubmission draft, CancellationToken cancellationToken = default)
    {
        throw new NotSupportedException();
    }

    public Task<bool> CanRemoveListingAfterDraftAsync(Guid listingId, Guid draftSubmissionId, CancellationToken cancellationToken = default)
    {
        throw new NotSupportedException();
    }

    public Task RemoveListingAsync(PropertyListing listing, CancellationToken cancellationToken = default)
    {
        throw new NotSupportedException();
    }

    public Task SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        throw new NotSupportedException();
    }
}
