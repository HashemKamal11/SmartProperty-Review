using SmartProperty.Application.Abstractions.Messaging;

namespace SmartProperty.Application.Marketplace.ListingSubmissions.Write.Update;

/// <summary>
/// Replaces the editable content of the acting user's own draft.
/// </summary>
/// <remarks>
/// A contract only; no handler exists and none is registered. <see cref="ExpectedRevision"/> is the optimistic
/// concurrency token the client read: a draft that has changed since is refused, never overwritten. The authority of
/// a draft is not editable here, and the draft's purpose is not a request input.
/// </remarks>
public sealed record UpdateListingDraftCommand(
    Guid SubmissionId,
    int ExpectedRevision,
    ListingDraftContentInput Content) : ICommand<ListingDraftResult>;
