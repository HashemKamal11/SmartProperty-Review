using SmartProperty.Application.Abstractions.Identity;
using SmartProperty.Application.Abstractions.Marketplace;
using SmartProperty.Application.Abstractions.Messaging;
using SmartProperty.Application.Abstractions.Persistence.Repositories;
using SmartProperty.Application.Abstractions.Time;
using SmartProperty.Common.Results;

namespace SmartProperty.Application.Marketplace.ListingSubmissions.Write.Submit;

/// <summary>
/// Submits the acting user's own draft for independent Platform Admin review, without publishing it.
/// </summary>
/// <remarks>
/// <para>
/// <b>Not registered.</b> Nothing resolves this handler in production.
/// </para>
/// <para>
/// <b>Everything is established under the locks.</b> After the account check and validation, the gate locks the
/// property and evaluates the grant under that lock. The draft is then read again and used for every decision: it must
/// be the revision the client saw, its content must still match the registry, its stored authority must still describe
/// the grant now in force, and its evidence must have exactly one matching Clean assessment each from the trusted
/// provider. The verification facts handed to the Domain come from the grant evaluation and nowhere else; the command
/// carries none.
/// </para>
/// <para>
/// <b>Evidence.</b> A draft with no evidence cannot be submitted, and a provider that cannot answer is not an answer.
/// Both are the temporary generic <see cref="WriteErrors.PrerequisitesUnverified"/>. The verifier also returns an
/// internal <see cref="EvidenceVerificationFailure"/> that separates an unavailable provider from an unacceptable
/// answer; nothing here puts it in a response, and a future caller with a logger can record that fixed category (and
/// nothing else about the evidence). The assessment call is made while the property and grant locks are held, so a
/// grant revoked meanwhile cannot be raced; the provider must answer quickly.
/// </para>
/// <para>
/// <b>Only the draft changes.</b> The Domain's revision-aware <c>Submit</c> is the only overload used. The listing, any
/// published version and every earlier submission are untouched, and approval and publication belong to the review.
/// </para>
/// </remarks>
public sealed class SubmitListingDraftCommandHandler(
    ICurrentUser currentUser,
    IUserRepository users,
    IListingSubmissionWriteRepository repository,
    IEvidenceAssessmentProvider evidence,
    IDateTimeProvider clock)
    : ICommandHandler<SubmitListingDraftCommand, SubmitListingDraftResult>
{
    public async Task<Result<SubmitListingDraftResult>> Handle(
        SubmitListingDraftCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        var acting = await ActingWriter.ResolveAsync(currentUser, users, cancellationToken);

        if (acting.Error is { } accountError)
        {
            return Result<SubmitListingDraftResult>.Failure(accountError);
        }

        if (SubmitListingDraftCommandValidator.Validate(command) is { } validationError)
        {
            return Result<SubmitListingDraftResult>.Failure(validationError);
        }

        var located = await OwnDraft.FindAsync(repository, acting.UserId, command.SubmissionId, cancellationToken);

        if (located is null)
        {
            return Result<SubmitListingDraftResult>.Failure(ListingSubmissionErrors.NotFound);
        }

        var now = clock.UtcNow;
        var locked = await OwnDraft.LockAsync(
            repository, acting.UserId, located, now, allowUnavailableProperty: false, cancellationToken);

        if (locked.IsFailure)
        {
            return Result<SubmitListingDraftResult>.Failure(locked.Error!);
        }

        var draft = locked.Value.Draft;
        var gate = locked.Value.Gate;

        if (OwnDraft.EnsureUnchanged(draft, command.ExpectedRevision) is { } staleError)
        {
            return Result<SubmitListingDraftResult>.Failure(staleError);
        }

        if (!CanonicalListingContent.Matches(draft.Content, gate.Property!))
        {
            return Result<SubmitListingDraftResult>.Failure(WriteErrors.PropertyChanged);
        }

        if (!DraftAuthority.IsStillValid(draft.Authority, gate.Authority!, now))
        {
            return Result<SubmitListingDraftResult>.Failure(WriteErrors.NotEligible);
        }

        var verification = await SubmissionEvidenceVerifier.VerifyAsync(draft, evidence, now, cancellationToken);

        if (verification.Result.IsFailure)
        {
            return Result<SubmitListingDraftResult>.Failure(verification.Result.Error!);
        }

        var facts = gate.SubmissionFacts!;

        draft.Submit(
            command.ExpectedRevision,
            acting.UserId,
            workspaceId: null,
            now,
            verification.Result.Value,
            facts.LegalAuthorityVerified,
            facts.WorkspaceOperationAuthorized,
            facts.ActingUserIsDeclaredOwnerVerified);

        if (await WriteCommit.SaveAsync(repository, WriteCommit.ForChange, cancellationToken) is { } conflict)
        {
            return Result<SubmitListingDraftResult>.Failure(conflict);
        }

        return Result<SubmitListingDraftResult>.Success(
            new SubmitListingDraftResult(draft.Id, draft.Status, draft.SubmittedAt!.Value));
    }
}
