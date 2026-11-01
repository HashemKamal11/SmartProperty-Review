using SmartProperty.Application.Abstractions.Persistence.Repositories;
using SmartProperty.Application.Marketplace.ListingSubmissions;
using SmartProperty.Domain.Marketplace;

namespace SmartProperty.UnitTests.TestDoubles;

/// <summary>
/// An in-memory stand-in that applies the same ownership rule the real repository must apply: a user only ever
/// sees rows that belong to them, and another user's row is indistinguishable from a missing one.
/// </summary>
/// <remarks>
/// Because it scopes by the acting user it is given, a handler that passed the wrong user id, or none, would see
/// the wrong rows or none, and the test would fail.
/// </remarks>
internal sealed class FakeSubmitterListingSubmissionReadRepository : ISubmitterListingSubmissionReadRepository
{
    private readonly List<(Guid Owner, SubmitterSubmissionDetail Detail)> _rows = [];

    /// <summary>When set, the repository throws it instead of answering.</summary>
    public Exception? ExceptionToThrow { get; set; }

    /// <summary>When set, <see cref="GetBySubmitterAsync"/> answers this row whoever asks (a faulty repository).</summary>
    public SubmitterSubmissionDetail? ForcedDetail { get; set; }

    public int ListCallCount { get; private set; }

    public ListCall? LastListCall { get; private set; }

    public int GetCallCount { get; private set; }

    public GetCall? LastGetCall { get; private set; }

    public CancellationToken CancellationToken { get; private set; }

    public void Add(Guid owner, SubmitterSubmissionDetail detail)
    {
        _rows.Add((owner, detail));
    }

    public Task<SubmitterSubmissionPage> ListBySubmitterAsync(
        Guid actingUserId,
        ListingPurpose? purpose,
        ListingSubmissionStatus? status,
        int skip,
        int take,
        CancellationToken cancellationToken = default)
    {
        ListCallCount++;
        LastListCall = new ListCall(actingUserId, purpose, status, skip, take);
        CancellationToken = cancellationToken;

        if (ExceptionToThrow is not null)
        {
            throw ExceptionToThrow;
        }

        var matches = _rows
            .Where(row => row.Owner == actingUserId)
            .Select(row => row.Detail)
            .Where(detail => purpose is null || detail.Purpose == purpose)
            .Where(detail => status is null || detail.Status == status)
            .OrderByDescending(detail => detail.CreatedAt)
            .ThenBy(detail => detail.SubmissionId)
            .ToArray();

        var items = matches
            .Skip(skip)
            .Take(take)
            .Select(ToSummary)
            .ToArray();

        return Task.FromResult(new SubmitterSubmissionPage(items, matches.Length));
    }

    public Task<SubmitterSubmissionDetail?> GetBySubmitterAsync(
        Guid actingUserId,
        Guid submissionId,
        CancellationToken cancellationToken = default)
    {
        GetCallCount++;
        LastGetCall = new GetCall(actingUserId, submissionId);
        CancellationToken = cancellationToken;

        if (ExceptionToThrow is not null)
        {
            throw ExceptionToThrow;
        }

        if (ForcedDetail is not null)
        {
            return Task.FromResult<SubmitterSubmissionDetail?>(ForcedDetail);
        }

        var detail = _rows
            .Where(row => row.Owner == actingUserId && row.Detail.SubmissionId == submissionId)
            .Select(row => row.Detail)
            .SingleOrDefault();

        return Task.FromResult(detail);
    }

    private static SubmitterSubmissionSummary ToSummary(SubmitterSubmissionDetail detail)
    {
        return new SubmitterSubmissionSummary(
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
            detail.CreatedAt,
            detail.SubmittedAt,
            detail.UpdatedAt,
            detail.IsCurrentlyPublished);
    }

    internal sealed record ListCall(
        Guid ActingUserId,
        ListingPurpose? Purpose,
        ListingSubmissionStatus? Status,
        int Skip,
        int Take);

    internal sealed record GetCall(Guid ActingUserId, Guid SubmissionId);
}
