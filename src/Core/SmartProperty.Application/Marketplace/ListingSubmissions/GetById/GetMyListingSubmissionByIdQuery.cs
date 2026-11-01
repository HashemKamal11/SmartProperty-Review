using SmartProperty.Application.Abstractions.Messaging;

namespace SmartProperty.Application.Marketplace.ListingSubmissions.GetById;

/// <summary>
/// One of the authenticated user's own listing submissions by its identifier.
/// </summary>
/// <remarks>
/// The id names the submission only. Whose submission it must be is never a request input: the handler reads the
/// acting user from the authenticated principal.
/// </remarks>
public sealed record GetMyListingSubmissionByIdQuery(Guid SubmissionId) : IQuery<SubmitterSubmissionDetail>;
