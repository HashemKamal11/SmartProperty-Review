using SmartProperty.Application.Abstractions.Persistence.Repositories;
using SmartProperty.Application.Marketplace.ListingSubmissions.Review;
using SmartProperty.Domain.Marketplace;

namespace SmartProperty.UnitTests.TestDoubles;

/// <summary>
/// An in-memory stand-in for the reviewer read repository. It receives no user identity and applies no
/// independence rule: reading does not depend on who submitted or owns a submission.
/// </summary>
internal sealed class FakeReviewerListingSubmissionReadRepository : IReviewerListingSubmissionReadRepository
{
    private readonly List<(ReviewQueueItem Item, Guid Submitter, Guid? LinkedOwner)> _rows = [];
    private readonly Dictionary<Guid, ReviewDetailLookup> _lookups = [];

    /// <summary>When set, the repository throws it instead of answering.</summary>
    public Exception? ExceptionToThrow { get; set; }

    public int ListCallCount { get; private set; }

    public ListCall? LastListCall { get; private set; }

    public int GetCallCount { get; private set; }

    public GetCall? LastGetCall { get; private set; }

    public CancellationToken CancellationToken { get; private set; }

    public void Add(ReviewQueueItem item, Guid submitter, Guid? linkedOwner = null)
    {
        _rows.Add((item, submitter, linkedOwner));
    }

    public void SetLookup(Guid submissionId, ReviewDetailLookup lookup)
    {
        _lookups[submissionId] = lookup;
    }

    public Task<ReviewQueuePage> ListForReviewAsync(
        ListingPurpose? purpose,
        ListingSubmissionStatus status,
        int skip,
        int take,
        CancellationToken cancellationToken = default)
    {
        ListCallCount++;
        LastListCall = new ListCall(purpose, status, skip, take);
        CancellationToken = cancellationToken;

        if (ExceptionToThrow is not null)
        {
            throw ExceptionToThrow;
        }

        var matches = _rows
            .Select(row => row.Item)
            .Where(item => item.Status == status)
            .Where(item => purpose is null || item.Purpose == purpose)
            .ToArray();

        var ordered = status == ListingSubmissionStatus.PendingReview
            ? matches.OrderBy(item => item.SubmittedAt).ThenBy(item => item.SubmissionId)
            : matches.OrderByDescending(item => item.SubmittedAt).ThenBy(item => item.SubmissionId);

        var items = ordered.Skip(skip).Take(take).ToArray();

        return Task.FromResult(new ReviewQueuePage(items, matches.Length));
    }

    public Task<ReviewDetailLookup> GetForReviewAsync(
        Guid submissionId,
        CancellationToken cancellationToken = default)
    {
        GetCallCount++;
        LastGetCall = new GetCall(submissionId);
        CancellationToken = cancellationToken;

        if (ExceptionToThrow is not null)
        {
            throw ExceptionToThrow;
        }

        return Task.FromResult(_lookups.GetValueOrDefault(submissionId) ?? ReviewDetailLookup.NotFound());
    }

    internal sealed record ListCall(
        ListingPurpose? Purpose,
        ListingSubmissionStatus Status,
        int Skip,
        int Take);

    internal sealed record GetCall(Guid SubmissionId);
}
