using SmartProperty.Application.Abstractions.Persistence.Repositories;
using SmartProperty.Application.Marketplace.ListingSubmissions.Review;
using SmartProperty.Domain.Marketplace;

namespace SmartProperty.Api.IntegrationTests.Infrastructure;

/// <summary>
/// A test-only stand-in for the reviewer read repository, which has no production implementation yet.
/// </summary>
/// <remarks>
/// It receives no user identity and applies no independence rule, because reading does not depend on who submitted
/// or owns a submission. It applies the rules the real query must apply: the queue never holds a Draft, and the
/// order is the documented one. It records what it was asked so tests can show what the API sent.
/// </remarks>
internal sealed class StubReviewerListingSubmissionReadRepository : IReviewerListingSubmissionReadRepository
{
    private readonly List<Row> _rows = [];
    private readonly Dictionary<Guid, ReviewDetailLookup> _forced = [];

    public Exception? ExceptionToThrow { get; set; }

    public int ListCallCount { get; private set; }

    public ListCall? LastListCall { get; private set; }

    public int GetCallCount { get; private set; }

    public GetCall? LastGetCall { get; private set; }

    public void Add(ReviewSubmissionDetail detail, Guid submitter, Guid? linkedOwner = null)
    {
        _rows.Add(new Row(detail, submitter, linkedOwner));
    }

    /// <summary>Makes a lookup answer with exactly this outcome, whatever the rows say.</summary>
    public void Force(Guid submissionId, ReviewDetailLookup lookup)
    {
        _forced[submissionId] = lookup;
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

        if (ExceptionToThrow is not null)
        {
            throw ExceptionToThrow;
        }

        var matches = _rows
            .Select(row => row.Detail)
            .Where(detail => detail.Status != ListingSubmissionStatus.Draft && detail.Status == status)
            .Where(detail => purpose is null || detail.Purpose == purpose)
            .ToArray();

        var ordered = status == ListingSubmissionStatus.PendingReview
            ? matches.OrderBy(detail => detail.SubmittedAt).ThenBy(detail => detail.SubmissionId)
            : matches.OrderByDescending(detail => detail.UpdatedAt).ThenBy(detail => detail.SubmissionId);

        var items = ordered.Skip(skip).Take(take).Select(ToItem).ToArray();

        return Task.FromResult(new ReviewQueuePage(items, matches.Length));
    }

    public Task<ReviewDetailLookup> GetForReviewAsync(
        Guid submissionId,
        CancellationToken cancellationToken = default)
    {
        GetCallCount++;
        LastGetCall = new GetCall(submissionId);

        if (ExceptionToThrow is not null)
        {
            throw ExceptionToThrow;
        }

        if (_forced.TryGetValue(submissionId, out var forced))
        {
            return Task.FromResult(forced);
        }

        var row = _rows.FirstOrDefault(candidate => candidate.Detail.SubmissionId == submissionId);

        if (row is null || row.Detail.Status == ListingSubmissionStatus.Draft)
        {
            return Task.FromResult(ReviewDetailLookup.NotFound());
        }

        return Task.FromResult(ReviewDetailLookup.Found(row.Detail));
    }

    private static ReviewQueueItem ToItem(ReviewSubmissionDetail detail)
    {
        return new ReviewQueueItem(
            detail.SubmissionId,
            detail.ListingId,
            detail.Purpose,
            detail.Version,
            detail.Status,
            detail.Title,
            detail.Price,
            detail.PropertyType,
            detail.Location,
            detail.Representation,
            detail.OwnerKind,
            detail.SubmittedAt,
            detail.EvidenceCount,
            detail.AllEvidenceClean);
    }

    private sealed record Row(ReviewSubmissionDetail Detail, Guid Submitter, Guid? LinkedOwner);

    internal sealed record ListCall(
        ListingPurpose? Purpose,
        ListingSubmissionStatus Status,
        int Skip,
        int Take);

    internal sealed record GetCall(Guid SubmissionId);
}
