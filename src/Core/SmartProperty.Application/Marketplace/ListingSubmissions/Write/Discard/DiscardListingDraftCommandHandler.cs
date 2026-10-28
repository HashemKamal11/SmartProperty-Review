using SmartProperty.Application.Abstractions.Identity;
using SmartProperty.Application.Abstractions.Marketplace;
using SmartProperty.Application.Abstractions.Messaging;
using SmartProperty.Application.Abstractions.Persistence.Repositories;
using SmartProperty.Application.Abstractions.Time;
using SmartProperty.Common.Results;

namespace SmartProperty.Application.Marketplace.ListingSubmissions.Write.Discard;

/// <summary>
/// Discards the acting user's own never-submitted draft, with its audit record, in one commit.
/// </summary>
/// <remarks>
/// <para>
/// <b>Not registered.</b> Nothing resolves this handler in production.
/// </para>
/// <para>
/// <b>Atomicity.</b> The handler tracks three changes (the draft's removal, the listing's removal when it would be left
/// empty, and the typed audit event) and then makes exactly one <c>SaveChangesAsync</c> call. The write repository
/// contract commits everything tracked, audit included, in one database transaction or none of it. The audit recorder
/// only adds to that unit of work; its implementation must not save on its own. No separate delete and audit commits
/// exist anywhere in this handler.
/// </para>
/// <para>
/// <b>What is never touched.</b> A submitted or reviewed version is not a draft and is refused before anything is
/// tracked. A listing that has other submissions, a publication, a withdrawal, a closure or a reservation is kept,
/// and so is any published predecessor. There is no Discarded status: the draft is removed and the audit event
/// outlives it.
/// </para>
/// <para>
/// <b>Authority.</b> A discard still needs a grant in force. The property may have since been archived or reserved, and
/// that does not stop an eligible user from removing their own draft.
/// </para>
/// </remarks>
public sealed class DiscardListingDraftCommandHandler(
    ICurrentUser currentUser,
    IUserRepository users,
    IListingSubmissionWriteRepository repository,
    IListingSubmissionAuditRecorder audit,
    IDateTimeProvider clock)
    : ICommandHandler<DiscardListingDraftCommand, DiscardListingDraftResult>
{
    public async Task<Result<DiscardListingDraftResult>> Handle(
        DiscardListingDraftCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        var acting = await ActingWriter.ResolveAsync(currentUser, users, cancellationToken);

        if (acting.Error is { } accountError)
        {
            return Result<DiscardListingDraftResult>.Failure(accountError);
        }

        if (DiscardListingDraftCommandValidator.Validate(command) is { } validationError)
        {
            return Result<DiscardListingDraftResult>.Failure(validationError);
        }

        var located = await OwnDraft.FindAsync(repository, acting.UserId, command.SubmissionId, cancellationToken);

        if (located is null)
        {
            return Result<DiscardListingDraftResult>.Failure(ListingSubmissionErrors.NotFound);
        }

        var now = clock.UtcNow;
        var locked = await OwnDraft.LockAsync(
            repository, acting.UserId, located, now, allowUnavailableProperty: true, cancellationToken);

        if (locked.IsFailure)
        {
            return Result<DiscardListingDraftResult>.Failure(locked.Error!);
        }

        var draft = locked.Value.Draft;
        var listing = locked.Value.Listing;

        if (OwnDraft.EnsureUnchanged(draft, command.ExpectedRevision) is { } staleError)
        {
            return Result<DiscardListingDraftResult>.Failure(staleError);
        }

        try
        {
            draft.EnsureCanDiscard(command.ExpectedRevision);
        }
        catch (InvalidOperationException)
        {
            // The Domain's refusal means the draft is not discardable as the client saw it. Nothing is tracked yet.
            return Result<DiscardListingDraftResult>.Failure(WriteErrors.StaleRevision);
        }

        var removeListing = await repository.CanRemoveListingAfterDraftAsync(listing.Id, draft.Id, cancellationToken);

        await repository.RemoveDraftAsync(draft, cancellationToken);

        if (removeListing)
        {
            await repository.RemoveListingAsync(listing, cancellationToken);
        }

        await audit.RecordAsync(
            new ListingDraftDiscardedAuditEvent(
                Guid.NewGuid(),
                now,
                acting.UserId,
                draft.Id,
                listing.Id,
                draft.PropertyId,
                draft.Purpose,
                draft.Version,
                removeListing),
            cancellationToken);

        if (await WriteCommit.SaveAsync(repository, WriteCommit.ForChange, cancellationToken) is { } conflict)
        {
            return Result<DiscardListingDraftResult>.Failure(conflict);
        }

        return Result<DiscardListingDraftResult>.Success(new DiscardListingDraftResult(removeListing));
    }
}
