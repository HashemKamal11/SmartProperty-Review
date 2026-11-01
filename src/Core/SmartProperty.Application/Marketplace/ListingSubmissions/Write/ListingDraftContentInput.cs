using SmartProperty.Domain.Marketplace;

namespace SmartProperty.Application.Marketplace.ListingSubmissions.Write;

/// <summary>
/// The editable content of a listing draft, as a submitter supplies it.
/// </summary>
/// <remarks>
/// Only what a submitter may claim. The location and property type come from the registry, the legal authority from a
/// verified grant, the evidence versions and digests from the evidence provider, and every verification and scan
/// result from the trusted sources. There is no member for any of them, so a request has nowhere to put one.
/// <see cref="EvidenceIds"/> may be empty on a draft; a submission requires evidence.
/// </remarks>
public sealed record ListingDraftContentInput(
    string Title,
    string Description,
    decimal Amount,
    string Currency,
    RentPeriod? RentPeriod,
    IReadOnlyList<Guid> EvidenceIds);
