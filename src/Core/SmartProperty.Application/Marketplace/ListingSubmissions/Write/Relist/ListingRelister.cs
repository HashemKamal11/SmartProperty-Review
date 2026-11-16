using SmartProperty.Application.Abstractions.Marketplace;
using SmartProperty.Application.Abstractions.Persistence.Repositories;
using SmartProperty.Application.Abstractions.Time;
using SmartProperty.Common.Results;
using SmartProperty.Domain.Marketplace;

namespace SmartProperty.Application.Marketplace.ListingSubmissions.Write.Relist;

/// <summary>What relisting created: a new version of the listing, already submitted for review.</summary>
public sealed record RelistListingResult(
    Guid SubmissionId,
    Guid ListingId,
    int Version,
    ListingSubmissionStatus Status,
    DateTimeOffset SubmittedAt);

/// <summary>
/// Brings a closed listing back by submitting a copy of its last approved version for review. The one place both the
/// owner's "relist" and the nightly automatic relisting do it.
/// </summary>
/// <remarks>
/// <para>
/// <b>Nothing is published.</b> The copy is a new submission in <c>PendingReview</c>; only an administrator's approval
/// publishes it, with every rule of an ordinary approval (the grant, the documents, and the commitments in force).
/// </para>
/// <para>
/// <b>Same values, current facts.</b> The title, description, prices, rental terms and document ids are copied. The
/// location and property type come from the registry now, the authority from the grant in force now, and each
/// document is resolved to its current version and must be Clean, exactly as for a draft the owner writes. A rental
/// window that has already ended is left out; if none is left, the copy is refused.
/// </para>
/// <para>
/// <b>Who acts.</b> Always the author of the last approved version, whose account must be Active and whose grant must
/// be in force. For the owner's request the caller must be that author; anyone else is the same not-found.
/// </para>
/// <para>
/// <b>Automatic relisting</b> additionally needs the owner's choice to still be on, the listing to have been closed by
/// an accepted request, and that commitment to have ended without a lasting deal (signature deadline passed, or rental
/// returned). An overdue commitment is recorded as expired in the same commit. If anything newer than the approved
/// version exists, the owner has already acted and nothing is done.
/// </para>
/// <para>
/// <b>Order.</b> Evidence is resolved before any lock (an external call), then property, grant, listing, commitment and
/// submissions are locked in the fixed order. Every decision uses the reads made under the locks, and one commit
/// writes everything or nothing.
/// </para>
/// </remarks>
public sealed class ListingRelister(
    IMarketplaceRequestWriteRepository repository,
    IUserRepository users,
    IEvidenceAssessmentProvider evidence,
    IDateTimeProvider clock)
{
    public Task<Result<RelistListingResult>> RelistByOwnerAsync(
        Guid actingUserId,
        Guid listingId,
        CancellationToken cancellationToken = default)
    {
        return RelistAsync(listingId, actingUserId, automatic: false, cancellationToken);
    }

    public Task<Result<RelistListingResult>> RelistAutomaticallyAsync(
        Guid listingId,
        CancellationToken cancellationToken = default)
    {
        return RelistAsync(listingId, requestedBy: null, automatic: true, cancellationToken);
    }

    private async Task<Result<RelistListingResult>> RelistAsync(
        Guid listingId,
        Guid? requestedBy,
        bool automatic,
        CancellationToken cancellationToken)
    {
        // ---- unlocked reads, only to learn what to lock and which evidence to resolve
        var known = await repository.FindListingAsync(listingId, cancellationToken);

        if (known is null)
        {
            return Failure(ListingSubmissionErrors.NotFound);
        }

        if (known.PublishedSubmissionId is not { } publishedId)
        {
            return Failure(requestedBy is null ? WriteErrors.NotRelistable : ListingSubmissionErrors.NotFound);
        }

        var knownPublished = await repository.GetSubmissionAsync(publishedId, cancellationToken);

        if (knownPublished is null)
        {
            return Failure(ListingSubmissionErrors.NotFound);
        }

        var author = knownPublished.Authority.ActingUserId;

        if (requestedBy is { } caller && caller != author)
        {
            return Failure(ListingSubmissionErrors.NotFound);
        }

        if (await ActiveAccountGate.CheckAsync(users, author, cancellationToken) is { } accountError)
        {
            return Failure(accountError);
        }

        var evidenceIds = knownPublished.Evidence.Select(reference => reference.EvidenceId).ToArray();
        var references = await DraftEvidence.ResolveAsync(
            evidence, author, known.PropertyId, known.Purpose, evidenceIds, cancellationToken);

        if (references.IsFailure)
        {
            return Failure(references.Error!);
        }

        // ---- locks, in the one fixed order
        var now = clock.UtcNow;
        var gate = await ListingWriteGate.EvaluateAsync(
            repository, author, known.PropertyId, known.Purpose, now, cancellationToken);

        if (!gate.IsOpen)
        {
            return Failure(gate.Error!);
        }

        var listing = await repository.GetListingByIdAsync(listingId, cancellationToken);

        if (listing is null || listing.PublishedSubmissionId != publishedId || !listing.CanBeRelisted)
        {
            return Failure(WriteErrors.NotRelistable);
        }

        if (automatic)
        {
            if (!listing.RelistAutomatically || listing.ClosedByCommitmentId is not { } commitmentId)
            {
                return Failure(WriteErrors.NotRelistable);
            }

            var commitment = await repository.GetCommitmentAsync(commitmentId, cancellationToken);

            if (commitment is null || !commitment.AllowsRelistAt(now))
            {
                return Failure(WriteErrors.NotRelistable);
            }

            // An overdue signature is recorded as expired in this same commit; anything else is left as it is.
            commitment.Expire(now);
        }

        var latest = await repository.GetLatestSubmissionAsync(listing.Id, cancellationToken);

        if (latest is null)
        {
            return Failure(WriteErrors.NotRelistable);
        }

        if (latest.Status is ListingSubmissionStatus.Draft or ListingSubmissionStatus.PendingReview)
        {
            return Failure(WriteErrors.OpenRevisionExists);
        }

        // Automatic relisting happens once: a newer version (a rejected copy, for instance) means the owner decides.
        if (automatic && latest.Id != publishedId)
        {
            return Failure(WriteErrors.NotRelistable);
        }

        var published = latest.Id == publishedId ? latest : await repository.GetSubmissionAsync(publishedId, cancellationToken);

        if (published is null
            || published.Authority.ActingUserId != author
            || !published.Evidence.Select(reference => reference.EvidenceId).SequenceEqual(evidenceIds))
        {
            return Failure(WriteErrors.NotRelistable);
        }

        // ---- the copy
        var input = RelistContent.From(published.Content, evidenceIds, now);

        if (input.IsFailure)
        {
            return Failure(input.Error!);
        }

        if (ListingDraftContentValidator.Validate(input.Value, listing.Purpose) is { } validationError)
        {
            return Failure(validationError);
        }

        var content = CanonicalListingContent.Build(gate.Property!, listing.Purpose, input.Value);

        if (content.IsFailure)
        {
            return Failure(content.Error!);
        }

        ListingSubmission submission;

        try
        {
            submission = new ListingSubmission(
                Guid.NewGuid(),
                listing,
                content.Value,
                DraftAuthority.FromFacts(gate.Authority!),
                references.Value,
                now,
                latest);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            return Failure(WriteErrors.NotRelistable);
        }

        var verification = await SubmissionEvidenceVerifier.VerifyAsync(submission, evidence, now, cancellationToken);

        if (verification.Result.IsFailure)
        {
            return Failure(verification.Result.Error!);
        }

        var facts = gate.SubmissionFacts!;

        submission.Submit(
            submission.Revision,
            author,
            workspaceId: null,
            now,
            verification.Result.Value,
            facts.LegalAuthorityVerified,
            facts.WorkspaceOperationAuthorized,
            facts.ActingUserIsDeclaredOwnerVerified);

        await repository.AddSubmissionAsync(submission, cancellationToken);

        if (await WriteCommit.SaveAsync(repository, WriteCommit.ForCreate, cancellationToken) is { } conflict)
        {
            return Failure(conflict);
        }

        return Result<RelistListingResult>.Success(new RelistListingResult(
            submission.Id, listing.Id, submission.Version, submission.Status, submission.SubmittedAt!.Value));
    }

    private static Result<RelistListingResult> Failure(Error error)
    {
        return Result<RelistListingResult>.Failure(error);
    }
}

/// <summary>Turns approved content back into the editable input a draft is built from.</summary>
internal static class RelistContent
{
    public static Result<ListingDraftContentInput> From(
        ListingSubmissionContent content,
        IReadOnlyList<Guid> evidenceIds,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(content);

        if (content.PricingOptions.Count == 0 || content.RentalTerms is not { } terms)
        {
            return Result<ListingDraftContentInput>.Success(new ListingDraftContentInput(
                content.Title, content.Description, content.Amount, content.Currency, content.RentPeriod, evidenceIds));
        }

        // A window is dated in the property's own calendar, so "today" is the property's today.
        var today = DateOnly.FromDateTime(
            TimeZoneInfo.ConvertTime(now, TimeZoneInfo.FindSystemTimeZoneById(terms.TimeZoneId)).DateTime);
        var windows = terms.Windows
            .Where(window => window.EndDate > today)
            .Select(window => new RentalWindowInput(window.StartDate, window.EndDate))
            .ToArray();

        if (windows.Length == 0)
        {
            return Result<ListingDraftContentInput>.Failure(WriteErrors.RelistDatesPassed);
        }

        return Result<ListingDraftContentInput>.Success(new ListingDraftContentInput(
            content.Title,
            content.Description,
            content.Amount,
            content.Currency,
            RentPeriod: null,
            evidenceIds,
            content.PricingOptions.Select(option => new RentPricingOptionInput(option.Period, option.Amount, option.Currency)).ToArray(),
            new RentalTermsInput(terms.CheckInTime, terms.CheckOutTime, windows, terms.MinimumStayDays)));
    }
}
