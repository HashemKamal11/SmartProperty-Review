using SmartProperty.Application.Abstractions.Messaging;
using SmartProperty.Common.Pagination;
using SmartProperty.Domain.Marketplace;

namespace SmartProperty.Application.Marketplace.ListingSubmissions.Review.Queue;

/// <summary>
/// The Platform Admin review queue.
/// </summary>
/// <remarks>
/// An absent <see cref="Status"/> means <see cref="ListingSubmissionStatus.PendingReview"/>, the queue a reviewer
/// wants. A Draft is not reviewable and is rejected. There is deliberately no reviewer, user, owner, Workspace or
/// search member: who is asking is never a request input, and the handler reads it from the authenticated principal.
/// </remarks>
public sealed record GetReviewQueueQuery(
    ListingSubmissionStatus? Status,
    ListingPurpose? Purpose,
    int? Page,
    int? PageSize) : IQuery<PagedList<ReviewQueueItem>>;
