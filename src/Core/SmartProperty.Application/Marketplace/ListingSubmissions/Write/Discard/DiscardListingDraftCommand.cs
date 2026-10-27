using SmartProperty.Application.Abstractions.Messaging;

namespace SmartProperty.Application.Marketplace.ListingSubmissions.Write.Discard;

/// <summary>
/// Discards the acting user's own never-submitted draft.
/// </summary>
/// <remarks>
/// A contract only; no handler exists and none is registered. A submitted version is never discarded, and a published
/// listing is never touched. The draft and its audit event are removed and recorded in one transaction.
/// </remarks>
public sealed record DiscardListingDraftCommand(Guid SubmissionId, int ExpectedRevision) : ICommand<DiscardListingDraftResult>;
