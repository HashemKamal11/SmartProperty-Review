using SmartProperty.Application.Abstractions.Messaging;

namespace SmartProperty.Application.Marketplace.ListingSubmissions.Review.Detail;

/// <summary>
/// One submission for Platform Admin review, by its identifier.
/// </summary>
/// <remarks>
/// The id names the submission only. Who is asking is never a request input: the handler reads the reviewer from
/// the authenticated principal.
/// </remarks>
public sealed record GetReviewSubmissionQuery(Guid SubmissionId) : IQuery<ReviewSubmissionDetail>;
