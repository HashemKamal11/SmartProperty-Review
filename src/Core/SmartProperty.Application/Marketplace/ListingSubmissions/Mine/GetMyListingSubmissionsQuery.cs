using SmartProperty.Application.Abstractions.Messaging;
using SmartProperty.Common.Pagination;
using SmartProperty.Domain.Marketplace;

namespace SmartProperty.Application.Marketplace.ListingSubmissions.Mine;

/// <summary>
/// The authenticated user's own listing submissions.
/// </summary>
/// <remarks>
/// There is deliberately no user, owner, legal-party, Workspace or property member: whose submissions to read is
/// never a request input. The handler reads the acting user from the authenticated principal.
/// </remarks>
public sealed record GetMyListingSubmissionsQuery(
    ListingPurpose? Purpose,
    ListingSubmissionStatus? Status,
    int? Page,
    int? PageSize) : IQuery<PagedList<SubmitterSubmissionSummary>>;
