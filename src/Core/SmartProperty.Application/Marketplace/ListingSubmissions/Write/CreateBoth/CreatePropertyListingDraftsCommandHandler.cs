using SmartProperty.Application.Abstractions.Identity;
using SmartProperty.Application.Abstractions.Marketplace;
using SmartProperty.Application.Abstractions.Messaging;
using SmartProperty.Application.Abstractions.Persistence.Repositories;
using SmartProperty.Application.Abstractions.Time;
using SmartProperty.Application.Marketplace.ListingSubmissions.Write.Relist;
using SmartProperty.Common.Results;
using SmartProperty.Domain.Marketplace;

namespace SmartProperty.Application.Marketplace.ListingSubmissions.Write.CreateBoth;

/// <summary>
/// Advertises one existing property for Sale, for Rent, or for both, with independent commercial terms.
/// </summary>
/// <remarks>
/// <para>
/// "Both" is a coordination of the two purpose-specific listings, not a third kind of listing. Each purpose has its
/// own grant, its own listing, its own versions and its own review, so advertising both needs a valid Sale grant
/// <b>and</b> a valid Rent grant. If either is missing the whole request is refused and nothing is written.
/// </para>
/// <para>
/// <b>One unit of work.</b> The property is locked once and both grants are read under that lock, in the order
/// property, grants, listings, submissions. Both drafts are then added and saved by a single commit, so either both
/// exist or neither does. Nothing is published: each draft goes through its own submission and independent review.
/// </para>
/// </remarks>
public sealed class CreatePropertyListingDraftsCommandHandler(
    ICurrentUser currentUser,
    IUserRepository users,
    IListingSubmissionWriteRepository repository,
    IEvidenceAssessmentProvider evidence,
    IDateTimeProvider clock)
    : ICommandHandler<CreatePropertyListingDraftsCommand, PropertyListingDraftsResult>
{
    public async Task<Result<PropertyListingDraftsResult>> Handle(
        CreatePropertyListingDraftsCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        var acting = await ActingWriter.ResolveAsync(currentUser, users, cancellationToken);

        if (acting.Error is { } accountError)
        {
            return Failure(accountError);
        }

        if (command.PropertyId == Guid.Empty)
        {
            return Failure(ListingSubmissionErrors.ValidationFailed("The property is required."));
        }

        var requested = new List<(ListingPurpose Purpose, ListingDraftContentInput Content)>();

        if (command.Sale is not null)
        {
            requested.Add((ListingPurpose.Sale, command.Sale));
        }

        if (command.Rent is not null)
        {
            requested.Add((ListingPurpose.Rent, command.Rent));
        }

        if (requested.Count == 0)
        {
            return Failure(ListingSubmissionErrors.ValidationFailed("Advertise the property for sale, for rent, or for both."));
        }

        if (command.Relist is { } mode && !Enum.IsDefined(mode))
        {
            return Failure(ListingSubmissionErrors.ValidationFailed("The relist mode is not supported."));
        }

        foreach (var (purpose, content) in requested)
        {
            if (ListingDraftContentValidator.Validate(content, purpose) is { } validationError)
            {
                return Failure(validationError);
            }
        }

        // Evidence is resolved by the trusted provider before any lock, one purpose at a time.
        var references = new Dictionary<ListingPurpose, IReadOnlyList<ListingEvidenceReference>>();

        foreach (var (purpose, content) in requested)
        {
            var resolved = await DraftEvidence.ResolveAsync(
                evidence, acting.UserId, command.PropertyId, purpose, content.EvidenceIds, cancellationToken);

            if (resolved.IsFailure)
            {
                return Failure(resolved.Error!);
            }

            references[purpose] = resolved.Value;
        }

        var now = clock.UtcNow;
        var gates = new Dictionary<ListingPurpose, ListingWriteGateResult>();

        // Property lock, then each grant under it. Every purpose must be eligible before anything is read further.
        foreach (var (purpose, _) in requested)
        {
            var gate = await ListingWriteGate.EvaluateAsync(
                repository, acting.UserId, command.PropertyId, purpose, now, cancellationToken);

            if (!gate.IsOpen)
            {
                return Failure(gate.Error!);
            }

            gates[purpose] = gate;
        }

        var prepared = new List<(ListingPurpose Purpose, PreparedDraft Draft)>();

        foreach (var (purpose, content) in requested)
        {
            if (ListingDraftContentValidator.PastAvailability(content, gates[purpose].Property!, now) is { } past)
            {
                return Failure(past);
            }

            var draft = await DraftCreation.PrepareAsync(
                repository, gates[purpose], purpose, content, references[purpose], now, cancellationToken);

            if (draft.IsFailure)
            {
                return Failure(draft.Error!);
            }

            prepared.Add((purpose, draft.Value));
        }

        foreach (var (_, draft) in prepared)
        {
            // The owner's automatic relisting choice applies to each side created; left out, the current choice stays.
            if (command.Relist is { } relist)
            {
                draft.Listing.SetRelistAutomatically(relist == RelistMode.Automatic);
            }

            if (draft.IsNewListing)
            {
                await repository.AddListingAsync(draft.Listing, cancellationToken);
            }

            await repository.AddSubmissionAsync(draft.Submission, cancellationToken);
        }

        if (await WriteCommit.SaveAsync(repository, WriteCommit.ForCreate, cancellationToken) is { } conflict)
        {
            return Failure(conflict);
        }

        ListingDraftResult? Result(ListingPurpose purpose)
        {
            var match = prepared.FirstOrDefault(item => item.Purpose == purpose);

            return match.Draft is null
                ? null
                : new ListingDraftResult(
                    match.Draft.Submission.Id, match.Draft.Listing.Id, match.Draft.Submission.Version, match.Draft.Submission.Revision);
        }

        return Result<PropertyListingDraftsResult>.Success(
            new PropertyListingDraftsResult(Result(ListingPurpose.Sale), Result(ListingPurpose.Rent)));
    }

    private static Result<PropertyListingDraftsResult> Failure(Error error)
    {
        return Result<PropertyListingDraftsResult>.Failure(error);
    }
}
