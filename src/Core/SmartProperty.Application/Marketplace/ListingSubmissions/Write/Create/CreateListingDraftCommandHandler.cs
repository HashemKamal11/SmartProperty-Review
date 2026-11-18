using SmartProperty.Application.Abstractions.Identity;
using SmartProperty.Application.Abstractions.Marketplace;
using SmartProperty.Application.Abstractions.Messaging;
using SmartProperty.Application.Abstractions.Persistence.Repositories;
using SmartProperty.Application.Abstractions.Time;
using SmartProperty.Application.Marketplace.ListingSubmissions.Write.Relist;
using SmartProperty.Common.Results;
using SmartProperty.Domain.Marketplace;

namespace SmartProperty.Application.Marketplace.ListingSubmissions.Write.Create;

/// <summary>
/// Creates a draft for a property and purpose, or the next version of an already reviewed one.
/// </summary>
/// <remarks>
/// <para>
/// <b>Not registered.</b> Nothing resolves this handler in production. It needs the write repository and the evidence
/// provider, neither of which has a production implementation yet.
/// </para>
/// <para>
/// <b>Order.</b> The acting user and the persisted account first; then the request is validated; then the evidence ids
/// are resolved by the trusted provider (an external call, made before any lock is taken); then the write gate locks
/// the property and evaluates the grant under that lock. Only then is the listing read, the slot checked and the draft
/// built. Nothing is added to the repository until every check has passed, so a refused request changes nothing.
/// </para>
/// <para>
/// <b>Slots.</b> There is one listing per property and purpose. An open draft or a version awaiting review means the
/// caller must update or wait; only a reviewed latest version allows a successor. The published version is never
/// touched: the draft is a new submission, and publication changes only when a reviewer approves it.
/// </para>
/// <para>
/// A race lost on the listing slot or the version number is the same <see cref="WriteErrors.OpenRevisionExists"/>.
/// </para>
/// </remarks>
public sealed class CreateListingDraftCommandHandler(
    ICurrentUser currentUser,
    IUserRepository users,
    IListingSubmissionWriteRepository repository,
    IEvidenceAssessmentProvider evidence,
    IDateTimeProvider clock)
    : ICommandHandler<CreateListingDraftCommand, ListingDraftResult>
{
    public async Task<Result<ListingDraftResult>> Handle(
        CreateListingDraftCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        var acting = await ActingWriter.ResolveAsync(currentUser, users, cancellationToken);

        if (acting.Error is { } accountError)
        {
            return Result<ListingDraftResult>.Failure(accountError);
        }

        if (CreateListingDraftCommandValidator.Validate(command) is { } validationError)
        {
            return Result<ListingDraftResult>.Failure(validationError);
        }

        if (command.Relist is { } mode && !Enum.IsDefined(mode))
        {
            return Result<ListingDraftResult>.Failure(ListingSubmissionErrors.ValidationFailed("The relist mode is not supported."));
        }

        var references = await DraftEvidence.ResolveAsync(
            evidence, acting.UserId, command.PropertyId, command.Purpose, command.Content.EvidenceIds, cancellationToken);

        if (references.IsFailure)
        {
            return Result<ListingDraftResult>.Failure(references.Error!);
        }

        var now = clock.UtcNow;
        var gate = await ListingWriteGate.EvaluateAsync(
            repository, acting.UserId, command.PropertyId, command.Purpose, now, cancellationToken);

        if (!gate.IsOpen)
        {
            return Result<ListingDraftResult>.Failure(gate.Error!);
        }

        if (ListingDraftContentValidator.PastAvailability(command.Content, gate.Property!, now) is { } past)
        {
            return Result<ListingDraftResult>.Failure(past);
        }

        var prepared = await DraftCreation.PrepareAsync(
            repository, gate, command.Purpose, command.Content, references.Value, now, cancellationToken);

        if (prepared.IsFailure)
        {
            return Result<ListingDraftResult>.Failure(prepared.Error!);
        }

        var (listing, isNewListing, submission) = prepared.Value;

        // The owner's automatic relisting choice is kept on the listing; left out, the current choice stays.
        if (command.Relist is { } relist)
        {
            listing.SetRelistAutomatically(relist == RelistMode.Automatic);
        }

        if (isNewListing)
        {
            await repository.AddListingAsync(listing, cancellationToken);
        }

        await repository.AddSubmissionAsync(submission, cancellationToken);

        if (await WriteCommit.SaveAsync(repository, WriteCommit.ForCreate, cancellationToken) is { } conflict)
        {
            return Result<ListingDraftResult>.Failure(conflict);
        }

        return Result<ListingDraftResult>.Success(
            new ListingDraftResult(submission.Id, listing.Id, submission.Version, submission.Revision));
    }
}
