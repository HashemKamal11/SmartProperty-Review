using SmartProperty.Application.Abstractions.Identity;
using SmartProperty.Application.Abstractions.Marketplace;
using SmartProperty.Application.Abstractions.Messaging;
using SmartProperty.Application.Abstractions.Persistence.Repositories;
using SmartProperty.Application.Abstractions.Time;
using SmartProperty.Common.Results;

namespace SmartProperty.Application.Marketplace.ListingSubmissions.Write.Update;

/// <summary>
/// Replaces the editable content of the acting user's own never-submitted draft.
/// </summary>
/// <remarks>
/// <para>
/// <b>Not registered.</b> Nothing resolves this handler in production.
/// </para>
/// <para>
/// <b>Order.</b> The acting user and account; validation; the draft is read only to learn which property to lock; the
/// evidence ids are resolved by the trusted provider before any lock; the gate locks the property and evaluates the
/// grant under that lock; and the draft is read again and used for every decision. A revoked or expired grant means the
/// draft can no longer be edited.
/// </para>
/// <para>
/// <b>Revision.</b> A draft whose revision is not exactly the one the client saw, or that has been submitted, is
/// <see cref="WriteErrors.StaleRevision"/> and is never overwritten. The Domain's own <c>UpdateDraft</c> performs the
/// change and increments the revision; location and type are rebuilt from the registry, never taken from the request.
/// A draft's authority is not touched.
/// </para>
/// </remarks>
public sealed class UpdateListingDraftCommandHandler(
    ICurrentUser currentUser,
    IUserRepository users,
    IListingSubmissionWriteRepository repository,
    IEvidenceAssessmentProvider evidence,
    IDateTimeProvider clock)
    : ICommandHandler<UpdateListingDraftCommand, ListingDraftResult>
{
    public async Task<Result<ListingDraftResult>> Handle(
        UpdateListingDraftCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        var acting = await ActingWriter.ResolveAsync(currentUser, users, cancellationToken);

        if (acting.Error is { } accountError)
        {
            return Result<ListingDraftResult>.Failure(accountError);
        }

        if (UpdateListingDraftCommandValidator.Validate(command) is { } validationError)
        {
            return Result<ListingDraftResult>.Failure(validationError);
        }

        var located = await OwnDraft.FindAsync(repository, acting.UserId, command.SubmissionId, cancellationToken);

        if (located is null)
        {
            return Result<ListingDraftResult>.Failure(ListingSubmissionErrors.NotFound);
        }

        // The draft's purpose is not a request input, so the rent period is checked against it here.
        if (ListingDraftContentValidator.Validate(command.Content, located.Purpose) is { } contentError)
        {
            return Result<ListingDraftResult>.Failure(contentError);
        }

        var references = await DraftEvidence.ResolveAsync(
            evidence, acting.UserId, located.PropertyId, located.Purpose, command.Content.EvidenceIds, cancellationToken);

        if (references.IsFailure)
        {
            return Result<ListingDraftResult>.Failure(references.Error!);
        }

        var now = clock.UtcNow;
        var locked = await OwnDraft.LockAsync(
            repository, acting.UserId, located, now, allowUnavailableProperty: false, cancellationToken);

        if (locked.IsFailure)
        {
            return Result<ListingDraftResult>.Failure(locked.Error!);
        }

        var draft = locked.Value.Draft;

        if (OwnDraft.EnsureUnchanged(draft, command.ExpectedRevision) is { } staleError)
        {
            return Result<ListingDraftResult>.Failure(staleError);
        }

        var content = CanonicalListingContent.Build(locked.Value.Gate.Property!, draft.Purpose, command.Content);

        if (content.IsFailure)
        {
            return Result<ListingDraftResult>.Failure(content.Error!);
        }

        draft.UpdateDraft(command.ExpectedRevision, content.Value, references.Value, now);

        if (await WriteCommit.SaveAsync(repository, WriteCommit.ForChange, cancellationToken) is { } conflict)
        {
            return Result<ListingDraftResult>.Failure(conflict);
        }

        return Result<ListingDraftResult>.Success(
            new ListingDraftResult(draft.Id, draft.ListingId, draft.Version, draft.Revision));
    }
}
