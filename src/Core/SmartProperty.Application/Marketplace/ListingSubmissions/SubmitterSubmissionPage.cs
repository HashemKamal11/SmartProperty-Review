namespace SmartProperty.Application.Marketplace.ListingSubmissions;

/// <summary>One page of the submitter's own submissions and the total number of matches.</summary>
public sealed record SubmitterSubmissionPage(
    IReadOnlyList<SubmitterSubmissionSummary> Items,
    long TotalCount);
