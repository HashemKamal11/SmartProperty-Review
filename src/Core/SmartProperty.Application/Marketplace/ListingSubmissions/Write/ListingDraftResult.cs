namespace SmartProperty.Application.Marketplace.ListingSubmissions.Write;

/// <summary>
/// What creating or updating a draft reports: which submission, which listing, which version, and the revision to
/// quote on the next change. The full submission is read back through the submitter read side.
/// </summary>
public sealed record ListingDraftResult(
    Guid SubmissionId,
    Guid ListingId,
    int Version,
    int Revision);
