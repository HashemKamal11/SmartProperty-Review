using SmartProperty.Application.Abstractions.Persistence.Repositories;
using SmartProperty.Application.Marketplace.ListingSubmissions;
using SmartProperty.Domain.Marketplace;

namespace SmartProperty.Api.IntegrationTests.Infrastructure;

/// <summary>
/// A test-only stand-in for the submitter read repository that applies the ownership rule the real one must apply in
/// its query: a submission is visible only to the user who acted on it.
/// </summary>
/// <remarks>
/// It exists only in test infrastructure; nothing in production can reach it. Because it scopes by the user id it is
/// given, a controller or handler that passed a wrong id, or a client-supplied one, would see the wrong rows or none,
/// and a test would fail. Ordering follows the repository contract: newest first, ending in the submission id.
/// </remarks>
internal sealed class StubSubmitterListingSubmissionReadRepository : ISubmitterListingSubmissionReadRepository
{
    private readonly List<(Guid Owner, SubmitterSubmissionDetail Detail)> _rows = [];

    /// <summary>When set, every read throws it instead of answering.</summary>
    public Exception? ExceptionToThrow { get; set; }

    public int ListCallCount { get; private set; }

    public ListCall? LastListCall { get; private set; }

    public int GetCallCount { get; private set; }

    public GetCall? LastGetCall { get; private set; }

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

        if (ExceptionToThrow is not null)
        {
            throw ExceptionToThrow;
        }

        var matches = _rows
            .Where(row => row.Owner == actingUserId)
            .Select(row => row.Detail)
            .Where(detail => purpose is null || detail.Purpose == purpose)
            .Where(detail => status is null || detail.Status == status)
            .OrderByDescending(detail => detail.UpdatedAt)
            .ThenBy(detail => detail.SubmissionId)
            .ToArray();

        var items = matches.Skip(skip).Take(take).Select(ToSummary).ToArray();

        return Task.FromResult(new SubmitterSubmissionPage(items, matches.Length));
    }

    public Task<SubmitterSubmissionDetail?> GetBySubmitterAsync(
        Guid actingUserId,
        Guid submissionId,
        CancellationToken cancellationToken = default)
    {
        GetCallCount++;
        LastGetCall = new GetCall(actingUserId, submissionId);

        if (ExceptionToThrow is not null)
        {
            throw ExceptionToThrow;
        }

        var row = _rows.FirstOrDefault(candidate => candidate.Owner == actingUserId && candidate.Detail.SubmissionId == submissionId);

        return Task.FromResult<SubmitterSubmissionDetail?>(row.Detail);
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
