using SmartProperty.Domain.Marketplace;

namespace SmartProperty.Application.Marketplace.ListingSubmissions.Write.Submit;

/// <summary>
/// The outcome of a submission: which submission, its new status, and when it was submitted.
/// </summary>
public sealed record SubmitListingDraftResult(
    Guid SubmissionId,
    ListingSubmissionStatus Status,
    DateTimeOffset SubmittedAt);
