using SmartProperty.Application.Abstractions.Messaging;

namespace SmartProperty.Application.Marketplace.ListingSubmissions.Write.Submit;

/// <summary>
/// Submits the acting user's own draft for independent Platform Admin review.
/// </summary>
/// <remarks>
/// A contract only; no handler exists and none is registered. The command names the draft and the revision the client
/// last saw, and nothing else. In particular it carries no verification: whether the legal authority holds, whether
/// the evidence exists and is Clean, and whether the property still matches are established by the handler from the
/// trusted sources, and a prerequisite that cannot be established rejects the submission. Review, approval and
/// publication are not part of this command.
/// </remarks>
public sealed record SubmitListingDraftCommand(Guid SubmissionId, int ExpectedRevision) : ICommand<SubmitListingDraftResult>;
