using SmartProperty.Application.Abstractions.Authorization;
using SmartProperty.Application.Abstractions.Identity;
using SmartProperty.Application.Abstractions.Marketplace;
using SmartProperty.Application.Abstractions.Persistence.Repositories;
using SmartProperty.Application.Abstractions.Storage;
using SmartProperty.Application.Abstractions.Time;
using SmartProperty.Application.Authorization;
using SmartProperty.Application.Marketplace.Authority;
using SmartProperty.Application.Marketplace.ListingSubmissions.Write;
using SmartProperty.Common.Results;
using SmartProperty.Domain.Marketplace;
using SmartProperty.Domain.OwnerVerification;
using SmartProperty.Domain.PropertyRegistry;

namespace SmartProperty.Application.Marketplace.AuthorityRequests;

/// <summary>
/// What staff do with authority requests and grants. Exactly two approvals: the legal reviewer checks the details and
/// documents; then a platform administrator, who must be a different person, approves, which issues the grants and
/// publishes the listings in one transaction. Either can send the request back to the requester. Grants can be revoked.
/// </summary>
/// <remarks>
/// <para>
/// <b>Permissions.</b> Approving or rejecting at legal review needs <see cref="PermissionCodes.MarketplaceAuthorityLegalReviewsRecord"/>;
/// issuing, or rejecting at issuance, needs <see cref="PermissionCodes.MarketplaceAuthorityGrantsIssue"/>; revoking needs
/// <see cref="PermissionCodes.MarketplaceAuthorityGrantsRevoke"/>. Reading the queue, a request, its documents and the grants
/// needs any of those or <see cref="PermissionCodes.MarketplaceAuthorityGrantsRead"/>.
/// </para>
/// <para>
/// <b>Independence.</b> Every decision asks <see cref="IReviewerIndependenceProvider"/>, the source listing reviews use.
/// Production uses the recorded relationships (verified accounts, approved owner profile, shared workspaces); unknown
/// (an organisation owner) is refused. Every decision also needs the reviewer's recorded no-conflict confirmation.
/// </para>
/// <para>
/// <b>The grants.</b> Issuance creates one <see cref="PropertyAuthorityGrant"/> per requested purpose through
/// <see cref="PropertyAuthorityGrant.Issue"/>, naming the owner's approved owner profile as the declared owner and this
/// request as the legal-review reference. Legal authority is recorded as verified because a distinct, authorized and
/// independent legal reviewer approved the documents stored on this request, before the issuer acted.
/// </para>
/// <para>
/// <b>Publication.</b> The administrator's approval is also the listing review: each listing is created from the stored
/// content with the new grant, submitted, and approved and published by the administrator, under the same rules a
/// listing review applies (property available, commitments, every document in force resolved as evidence and Clean
/// by the malware scanner, administrator independent). Nothing is approved while a document is not Clean.
/// </para>
/// </remarks>
public sealed class StaffAuthorityRequests(
    ICurrentUser currentUser,
    IUserRepository users,
    IPermissionChecker permissions,
    IReviewerIndependenceProvider independence,
    IPropertyRepository properties,
    IOwnerProfileRepository ownerProfiles,
    IAuthorityRequestRepository repository,
    IMarketplaceRequestWriteRepository listings,
    AuthorityDocumentEvidenceProvider documentEvidence,
    IPrivateDocumentStorage storage,
    IDateTimeProvider clock,
    SmartProperty.Application.Abstractions.Ai.IListingSearchIndexer? indexer = null)
{
    public const int DefaultPageSize = 20;
    public const int MaxPageSize = 100;

    private static readonly string[] ReadPermissions =
    [
        PermissionCodes.MarketplaceAuthorityGrantsRead,
        PermissionCodes.MarketplaceAuthorityLegalReviewsRecord,
        PermissionCodes.MarketplaceAuthorityGrantsIssue,
        PermissionCodes.MarketplaceAuthorityGrantsRevoke
    ];

    public async Task<Result<AuthorityRequestPage>> ListAsync(
        string? status, int? page, int? pageSize, CancellationToken cancellationToken = default)
    {
        var staff = await ReaderAsync(cancellationToken);

        if (staff.Error is { } error)
        {
            return Result<AuthorityRequestPage>.Failure(error);
        }

        AuthorityRequestStatus? filter = null;

        if (!string.IsNullOrWhiteSpace(status))
        {
            if (!OwnerAuthorityRequests.TryParseName(status, out AuthorityRequestStatus parsed) || parsed == AuthorityRequestStatus.Draft)
            {
                return Result<AuthorityRequestPage>.Failure(AuthorityRequestErrors.Validation(
                    "status must be PendingLegalReview, PendingIssuance, Approved or Withdrawn."));
            }

            filter = parsed;
        }

        var number = page ?? 1;
        var size = pageSize ?? DefaultPageSize;

        if (number < 1 || size is < 1 or > MaxPageSize)
        {
            return Result<AuthorityRequestPage>.Failure(AuthorityRequestErrors.Validation(
                $"page must be 1 or more and pageSize 1 to {MaxPageSize}."));
        }

        var (items, total) = await repository.ListForStaffAsync(filter, (number - 1) * size, size, cancellationToken);

        return Result<AuthorityRequestPage>.Success(new AuthorityRequestPage(
            items.Select(request => AuthorityRequestView.From(request, forStaff: true)).ToArray(), number, size, total));
    }

    public async Task<Result<AuthorityRequestView>> GetAsync(Guid requestId, CancellationToken cancellationToken = default)
    {
        var loaded = await LoadForReadAsync(requestId, cancellationToken);

        return loaded.IsFailure
            ? Result<AuthorityRequestView>.Failure(loaded.Error!)
            : Result<AuthorityRequestView>.Success(AuthorityRequestView.From(loaded.Value, forStaff: true));
    }

    public async Task<Result<IReadOnlyList<AuthorityDocumentView>>> ListDocumentsAsync(
        Guid requestId, bool includeHistory, CancellationToken cancellationToken = default)
    {
        var loaded = await LoadForReadAsync(requestId, cancellationToken);

        return loaded.IsFailure
            ? Result<IReadOnlyList<AuthorityDocumentView>>.Failure(loaded.Error!)
            : Result<IReadOnlyList<AuthorityDocumentView>>.Success(
                OwnerAuthorityRequests.DocumentList(loaded.Value, includeHistory, forStaff: true));
    }

    public async Task<Result<AuthorityDocumentDetailView>> GetDocumentAsync(
        Guid requestId, Guid documentId, CancellationToken cancellationToken = default)
    {
        var loaded = await LoadForReadAsync(requestId, cancellationToken);

        if (loaded.IsFailure)
        {
            return Result<AuthorityDocumentDetailView>.Failure(loaded.Error!);
        }

        return loaded.Value.VersionsOf(documentId).Count == 0
            ? Result<AuthorityDocumentDetailView>.Failure(AuthorityRequestErrors.DocumentNotFound)
            : Result<AuthorityDocumentDetailView>.Success(AuthorityDocumentDetailView.From(loaded.Value, documentId, forStaff: true));
    }

    public async Task<Result<AuthorityDocumentFile>> OpenDocumentAsync(
        Guid requestId, Guid documentId, Guid? versionId = null, CancellationToken cancellationToken = default)
    {
        var loaded = await LoadForReadAsync(requestId, cancellationToken);

        return loaded.IsFailure
            ? Result<AuthorityDocumentFile>.Failure(loaded.Error!)
            : await OwnerAuthorityRequests.OpenAsync(storage, loaded.Value, documentId, versionId, cancellationToken);
    }

    /// <summary>The property as it is now, for the reviewers to check against the documents.</summary>
    public async Task<Result<SmartProperty.Application.PropertyRegistry.PropertyDetails>> GetPropertyAsync(
        Guid requestId, CancellationToken cancellationToken = default)
    {
        var loaded = await LoadForReadAsync(requestId, cancellationToken);

        if (loaded.IsFailure)
        {
            return Result<SmartProperty.Application.PropertyRegistry.PropertyDetails>.Failure(loaded.Error!);
        }

        var details = await properties.GetDetailsByIdAsync(loaded.Value.PropertyId, cancellationToken);

        return details is null
            ? Result<SmartProperty.Application.PropertyRegistry.PropertyDetails>.Failure(AuthorityRequestErrors.PropertyNotFound)
            : Result<SmartProperty.Application.PropertyRegistry.PropertyDetails>.Success(details);
    }

    /// <summary>
    /// The legal reviewer accepts the details, the property data and the documents. Moves the request to the
    /// administrator; nothing is issued or published.
    /// </summary>
    public async Task<Result<AuthorityRequestView>> ApproveLegalReviewAsync(
        Guid requestId, string? note, bool? noConflictOfInterest, CancellationToken cancellationToken = default)
    {
        var staff = await DeciderAsync(PermissionCodes.MarketplaceAuthorityLegalReviewsRecord, cancellationToken);

        if (staff.Error is { } gateError)
        {
            return Result<AuthorityRequestView>.Failure(gateError);
        }

        var request = requestId == Guid.Empty ? null : await repository.GetAsync(requestId, cancellationToken);

        if (request is null)
        {
            return Result<AuthorityRequestView>.Failure(AuthorityRequestErrors.NotFound);
        }

        if (request.Status != AuthorityRequestStatus.PendingLegalReview)
        {
            return Result<AuthorityRequestView>.Failure(AuthorityRequestErrors.InvalidState);
        }

        if (noConflictOfInterest != true)
        {
            return Result<AuthorityRequestView>.Failure(AuthorityRequestErrors.ConflictAttestationRequired);
        }

        if (request.Missing.Count > 0)
        {
            return Result<AuthorityRequestView>.Failure(AuthorityRequestErrors.Incomplete(request.Missing));
        }

        var property = await properties.GetByIdAsync(request.PropertyId, cancellationToken);

        if (property is null || property.Status != PropertyStatus.Active || property.OwnerUserId != request.RequesterUserId)
        {
            return Result<AuthorityRequestView>.Failure(AuthorityRequestErrors.CannotIssue);
        }

        var independent = await IndependentAsync(staff.UserId, request, authorized: true, cancellationToken);

        if (independent.IsFailure)
        {
            return Result<AuthorityRequestView>.Failure(independent.Error!);
        }

        try
        {
            request.ApproveLegalReview(staff.UserId, note, property.UpdatedAt, attestedNoConflict: true, clock.UtcNow);
        }
        catch (ArgumentException exception)
        {
            return Result<AuthorityRequestView>.Failure(AuthorityRequestErrors.Validation(Describe(exception)));
        }

        return await SaveAsync(request, cancellationToken);
    }

    /// <summary>
    /// Sends the request back to the requester to complete, at its current stage: legal review (legal reviewer) or the
    /// final decision (administrator, who must not be the legal reviewer). The reason is shown to the requester; after
    /// they resubmit, both reviews are done again.
    /// </summary>
    public Task<Result<AuthorityRequestView>> RequestCompletionAsync(
        Guid requestId, string? reason, bool? noConflictOfInterest, CancellationToken cancellationToken = default) =>
        DecideAsync(requestId, reason, noConflictOfInterest,
            (request, userId, text, at) => request.RequestCompletion(userId, text, attestedNoConflict: true, at), cancellationToken);

    /// <summary>Rejects the request for a reason, at its current stage. Final; the requester may start a new request.</summary>
    public Task<Result<AuthorityRequestView>> RejectAsync(
        Guid requestId, string? reason, bool? noConflictOfInterest, CancellationToken cancellationToken = default) =>
        DecideAsync(requestId, reason, noConflictOfInterest,
            (request, userId, text, at) => request.Reject(userId, text, attestedNoConflict: true, at), cancellationToken);

    private async Task<Result<AuthorityRequestView>> DecideAsync(
        Guid requestId,
        string? reason,
        bool? noConflictOfInterest,
        Action<AuthorityRequest, Guid, string, DateTimeOffset> decide,
        CancellationToken cancellationToken)
    {
        var account = await ActingWriter.ResolveAsync(currentUser, users, cancellationToken);

        if (account.Error is { } accountError)
        {
            return Result<AuthorityRequestView>.Failure(accountError);
        }

        var request = requestId == Guid.Empty ? null : await repository.GetAsync(requestId, cancellationToken);

        // Without any staff permission the caller learns nothing about the request.
        if (!await HoldsAnyAsync(account.UserId, ReadPermissions, cancellationToken))
        {
            return Result<AuthorityRequestView>.Failure(AuthorityRequestErrors.Forbidden);
        }

        if (request is null || request.Status == AuthorityRequestStatus.Draft)
        {
            return Result<AuthorityRequestView>.Failure(AuthorityRequestErrors.NotFound);
        }

        var needed = request.Status switch
        {
            AuthorityRequestStatus.PendingLegalReview => PermissionCodes.MarketplaceAuthorityLegalReviewsRecord,
            AuthorityRequestStatus.PendingIssuance => PermissionCodes.MarketplaceAuthorityGrantsIssue,
            _ => null
        };

        if (needed is null)
        {
            return Result<AuthorityRequestView>.Failure(AuthorityRequestErrors.InvalidState);
        }

        if (!await HoldsAsync(account.UserId, needed, cancellationToken))
        {
            return Result<AuthorityRequestView>.Failure(AuthorityRequestErrors.Forbidden);
        }

        if (request.Status == AuthorityRequestStatus.PendingIssuance && request.LegalReviewedByUserId == account.UserId)
        {
            return Result<AuthorityRequestView>.Failure(AuthorityRequestErrors.SamePersonForBothDecisions);
        }

        if (string.IsNullOrWhiteSpace(reason))
        {
            return Result<AuthorityRequestView>.Failure(AuthorityRequestErrors.Validation("A reason is required."));
        }

        if (noConflictOfInterest != true)
        {
            return Result<AuthorityRequestView>.Failure(AuthorityRequestErrors.ConflictAttestationRequired);
        }

        var independent = await IndependentAsync(account.UserId, request, authorized: true, cancellationToken);

        if (independent.IsFailure)
        {
            return Result<AuthorityRequestView>.Failure(independent.Error!);
        }

        try
        {
            decide(request, account.UserId, reason, clock.UtcNow);
        }
        catch (ArgumentException exception)
        {
            return Result<AuthorityRequestView>.Failure(AuthorityRequestErrors.Validation(Describe(exception)));
        }

        return await SaveAsync(request, cancellationToken);
    }

    /// <summary>
    /// The administrator's final approval: re-checks everything (property unchanged since the legal review, every
    /// document in force Clean, owner still approved, both people still independent), then issues one grant per
    /// purpose, valid for <paramref name="validDays"/> (default 365), and publishes the listings, in one transaction.
    /// </summary>
    public async Task<Result<AuthorityRequestView>> IssueAsync(
        Guid requestId, int? validDays, bool? noConflictOfInterest, CancellationToken cancellationToken = default)
    {
        var issuer = await DeciderAsync(PermissionCodes.MarketplaceAuthorityGrantsIssue, cancellationToken);

        if (issuer.Error is { } gateError)
        {
            return Result<AuthorityRequestView>.Failure(gateError);
        }

        var days = validDays ?? AuthorityRequest.DefaultValidDays;

        if (days is < 1 or > AuthorityRequest.MaxValidDays)
        {
            return Result<AuthorityRequestView>.Failure(AuthorityRequestErrors.Validation(
                $"validDays must be 1 to {AuthorityRequest.MaxValidDays}."));
        }

        var request = requestId == Guid.Empty ? null : await repository.GetAsync(requestId, cancellationToken);

        if (request is null)
        {
            return Result<AuthorityRequestView>.Failure(AuthorityRequestErrors.NotFound);
        }

        if (request.Status != AuthorityRequestStatus.PendingIssuance
            || request.LegalReviewedByUserId is not { } legalReviewerId
            || request.LegalReviewedAt is not { } legalReviewedAt)
        {
            return Result<AuthorityRequestView>.Failure(AuthorityRequestErrors.InvalidState);
        }

        if (legalReviewerId == issuer.UserId)
        {
            return Result<AuthorityRequestView>.Failure(AuthorityRequestErrors.SamePersonForBothDecisions);
        }

        // What the legal reviewer approved must still describe the world: the property, its owner, the owner's approval.
        var property = await properties.GetByIdAsync(request.PropertyId, cancellationToken);
        var profile = await ownerProfiles.GetByUserIdAsync(request.RequesterUserId, cancellationToken);

        if (property is null
            || property.Status != PropertyStatus.Active
            || property.OwnerUserId != request.RequesterUserId
            || profile is null
            || profile.Id != request.RequesterProfileId
            || profile.Status != OwnerVerificationStatus.Approved)
        {
            return Result<AuthorityRequestView>.Failure(AuthorityRequestErrors.CannotIssue);
        }

        var issuerContext = await IndependentAsync(issuer.UserId, request, authorized: true, cancellationToken);

        if (issuerContext.IsFailure)
        {
            return Result<AuthorityRequestView>.Failure(issuerContext.Error!);
        }

        // The legal reviewer's authority and independence are re-established now, not trusted from the earlier decision.
        var legalReviewerStillAuthorized = await HoldsAsync(legalReviewerId, PermissionCodes.MarketplaceAuthorityLegalReviewsRecord, cancellationToken);
        var legalReviewerContext = await IndependentAsync(legalReviewerId, request, legalReviewerStillAuthorized, cancellationToken);

        if (legalReviewerContext.IsFailure)
        {
            return Result<AuthorityRequestView>.Failure(AuthorityRequestErrors.CannotIssue);
        }

        if (request.Capacity is not { } capacity)
        {
            return Result<AuthorityRequestView>.Failure(AuthorityRequestErrors.InvalidState);
        }

        if (noConflictOfInterest != true)
        {
            return Result<AuthorityRequestView>.Failure(AuthorityRequestErrors.ConflictAttestationRequired);
        }

        // Every required document is still there and every document in force passed its malware scan.
        if (request.Missing.Count > 0)
        {
            return Result<AuthorityRequestView>.Failure(AuthorityRequestErrors.CannotPublish(
                $"the request is no longer complete ({string.Join(", ", request.Missing)})."));
        }

        // The legal reviewer reviewed the property as it was; a later change needs a new review.
        if (request.ReviewedPropertyUpdatedAt != property.UpdatedAt)
        {
            return Result<AuthorityRequestView>.Failure(AuthorityRequestErrors.PropertyChangedSinceReview);
        }

        var evidenceIds = request.CurrentDocuments.Select(document => document.DocumentId).ToArray();

        var contents = new Dictionary<ListingPurpose, ListingDraftContentInput>();

        foreach (var purpose in request.Purposes)
        {
            if (AuthorityListingContent.Deserialize(request.ListingContentFor(purpose)) is not { } content)
            {
                return Result<AuthorityRequestView>.Failure(AuthorityRequestErrors.CannotPublish($"the {purpose} listing content is missing."));
            }

            // The listing's evidence is the request's own documents in force, never ids a client typed.
            contents[purpose] = content with { EvidenceIds = evidenceIds };
        }

        var now = clock.UtcNow;
        var obstacle = await OwnerAuthorityRequests.GrantObstacleAsync(
            repository, request.RequesterUserId, request.PropertyId, request.RequestsSale, request.RequestsRent, now, cancellationToken);

        if (obstacle is not null)
        {
            return Result<AuthorityRequestView>.Failure(obstacle);
        }

        // From here on everything happens under the property lock in one transaction: the grants, the request's
        // approval, and each listing created, submitted and published. Any refusal returns before the commit, and the
        // abandoned transaction is rolled back, so nothing is half approved.
        var locked = await listings.LockPropertyAsync(request.PropertyId, cancellationToken);

        if (locked is null || locked.PropertyId != request.PropertyId || !locked.IsActive || locked.HasMarketplaceReservation)
        {
            return Result<AuthorityRequestView>.Failure(AuthorityRequestErrors.CannotPublish("the property is not available."));
        }

        // The same commitment rules as a listing review: nothing is advertised over a sale in force, and a sale is not
        // advertised over a daily or weekly rental in force.
        var active = (await listings.GetOpenCommitmentsAsync(request.PropertyId, cancellationToken))
            .Where(commitment => commitment.IsActiveAt(now))
            .ToArray();

        if (active.Any(commitment => commitment.Kind == CommitmentKind.Sale))
        {
            return Result<AuthorityRequestView>.Failure(AuthorityRequestErrors.CannotPublish("the property is committed to a sale."));
        }

        if (request.RequestsSale && active.Any(commitment => commitment.IsShortStay))
        {
            return Result<AuthorityRequestView>.Failure(AuthorityRequestErrors.CannotPublish("a daily or weekly rental is in force."));
        }

        var validUntil = now.AddDays(days);
        var declaredOwner = DeclaredOwner(request);
        var ownerVerified = capacity == ListingRepresentationKind.Owner ? true : (bool?)null;
        var staged = new List<(ListingSubmission Submission, PropertyListing Listing)>();
        Guid? saleGrantId = null;
        Guid? rentGrantId = null;

        try
        {
            foreach (var purpose in request.Purposes)
            {
                var authority = new ListingLegalAuthority(
                    request.PropertyId, purpose, declaredOwner, request.RequesterUserId, capacity, null, now, validUntil);
                var grant = PropertyAuthorityGrant.Issue(
                    Guid.NewGuid(), authority, issuerContext.Value, request.Id, legalReviewerContext.Value,
                    legalReviewedAt, now, legalAuthorityVerified: true);

                repository.AddGrant(grant);

                if (purpose == ListingPurpose.Sale)
                {
                    saleGrantId = grant.Id;
                }
                else
                {
                    rentGrantId = grant.Id;
                }

                // The listing is created from the stored content with exactly the grant just issued, as a listing
                // draft would be after the grant existed.
                var facts = new ListingAuthorityFacts(
                    grant.Id, grant.Version, request.PropertyId, purpose, request.RequesterUserId,
                    declaredOwner.Kind, declaredOwner.LegalPartyReferenceId, declaredOwner.LinkedUserId, capacity, now, validUntil);
                var gate = new ListingWriteGateResult(null, locked, facts, new SubmissionVerificationFacts(true, ownerVerified, null));
                var references = await DraftEvidence.ResolveAsync(
                    documentEvidence, request.RequesterUserId, request.PropertyId, purpose, contents[purpose].EvidenceIds, cancellationToken);

                if (references.IsFailure)
                {
                    return Result<AuthorityRequestView>.Failure(AuthorityRequestErrors.CannotPublish(references.Error!.Description));
                }

                // The fixed lock order is property, grant (shared), listing, submissions. The grant issued above is not
                // written yet, so this read finds none (or an old revoked one) and takes its place in that order.
                await listings.GetGrantForSharedLockAsync(request.RequesterUserId, request.PropertyId, purpose, cancellationToken);

                var prepared = await DraftCreation.PrepareAsync(
                    listings, gate, purpose, contents[purpose], references.Value, now, cancellationToken);

                if (prepared.IsFailure)
                {
                    return Result<AuthorityRequestView>.Failure(AuthorityRequestErrors.CannotPublish(prepared.Error!.Description));
                }

                if (prepared.Value.IsNewListing)
                {
                    await listings.AddListingAsync(prepared.Value.Listing, cancellationToken);
                }

                await listings.AddSubmissionAsync(prepared.Value.Submission, cancellationToken);
                staged.Add((prepared.Value.Submission, prepared.Value.Listing));
            }

            request.MarkIssued(issuer.UserId, validUntil, saleGrantId, rentGrantId, attestedNoConflict: true, now);
        }
        catch (InvalidOperationException)
        {
            // The Domain refused the grant as a whole; nothing has been saved.
            return Result<AuthorityRequestView>.Failure(AuthorityRequestErrors.CannotIssue);
        }

        // Written inside the still-open transaction, so a new listing exists before its first publication points at it.
        if (!await repository.TrySaveAsync(cancellationToken))
        {
            return Result<AuthorityRequestView>.Failure(AuthorityRequestErrors.ConcurrentChange);
        }

        // Each listing then goes through the same two recorded transitions as any listing: submitted (with its evidence
        // assessments), then approved and published. The history rules require them as separate writes; both stay in
        // this transaction.
        var assessed = new List<(ListingSubmission Submission, PropertyListing Listing, IReadOnlyList<ListingEvidenceAssessment> Assessments)>();

        foreach (var (submission, listing) in staged)
        {
            var verification = await SubmissionEvidenceVerifier.VerifyAsync(submission, documentEvidence, now, cancellationToken);

            if (verification.Result.IsFailure)
            {
                return Result<AuthorityRequestView>.Failure(AuthorityRequestErrors.CannotPublish(verification.Result.Error!.Description));
            }

            try
            {
                submission.Submit(
                    submission.Revision, request.RequesterUserId, workspaceId: null, now,
                    verification.Result.Value, legalAuthorityVerified: true, workspaceOperationAuthorized: null, ownerVerified);
            }
            catch (Exception exception) when (exception is InvalidOperationException or ArgumentException)
            {
                return Result<AuthorityRequestView>.Failure(AuthorityRequestErrors.CannotPublish("the listing failed its submission checks."));
            }

            assessed.Add((submission, listing, verification.Result.Value));
        }

        if (!await repository.TrySaveAsync(cancellationToken))
        {
            return Result<AuthorityRequestView>.Failure(AuthorityRequestErrors.ConcurrentChange);
        }

        foreach (var (submission, listing, assessments) in assessed)
        {
            var current = listing.PublishedSubmissionId is { } publishedId
                ? await listings.GetSubmissionAsync(publishedId, cancellationToken)
                : null;

            try
            {
                submission.ApproveAndPublish(
                    Guid.NewGuid(), listing, current, issuerContext.Value, now, propertyAvailable: true,
                    assessments, legalAuthorityVerified: true, workspaceOperationAuthorized: null, ownerVerified);
            }
            catch (Exception exception) when (exception is InvalidOperationException or ArgumentException)
            {
                return Result<AuthorityRequestView>.Failure(AuthorityRequestErrors.CannotPublish("the listing failed its publication checks."));
            }
        }

        if (await WriteCommit.SaveAsync(listings, WriteCommit.ForChange, cancellationToken) is not null)
        {
            return Result<AuthorityRequestView>.Failure(AuthorityRequestErrors.ConcurrentChange);
        }

        // The commit is the decision. Telling the search index is best effort and never changes it.
        if (indexer is not null)
        {
            foreach (var purpose in request.Purposes)
            {
                await indexer.IndexAsync(request.PropertyId, purpose, cancellationToken);
            }
        }

        return Result<AuthorityRequestView>.Success(AuthorityRequestView.From(request, forStaff: true));
    }

    /// <summary>Revokes a grant permanently. Listings it allowed are no longer eligible for new submissions or approvals.</summary>
    public async Task<Result<AuthorityGrantSummary>> RevokeGrantAsync(
        Guid grantId, long? expectedVersion, CancellationToken cancellationToken = default)
    {
        var staff = await DeciderAsync(PermissionCodes.MarketplaceAuthorityGrantsRevoke, cancellationToken);

        if (staff.Error is { } gateError)
        {
            return Result<AuthorityGrantSummary>.Failure(gateError);
        }

        if (expectedVersion is not { } version)
        {
            return Result<AuthorityGrantSummary>.Failure(AuthorityRequestErrors.Validation("expectedVersion is required."));
        }

        var grant = grantId == Guid.Empty ? null : await repository.GetGrantAsync(grantId, cancellationToken);

        if (grant is null)
        {
            return Result<AuthorityGrantSummary>.Failure(AuthorityRequestErrors.GrantNotFound);
        }

        var owner = grant.Authority.DeclaredOwner;
        var context = await IndependentAsync(staff.UserId, grant.GranteeUserId, owner, authorized: true, cancellationToken);

        if (context.IsFailure)
        {
            return Result<AuthorityGrantSummary>.Failure(context.Error!);
        }

        var now = clock.UtcNow;

        try
        {
            grant.Revoke(version, context.Value, now);
        }
        catch (InvalidOperationException)
        {
            return Result<AuthorityGrantSummary>.Failure(AuthorityRequestErrors.GrantNotRevocable);
        }

        return await repository.TrySaveAsync(cancellationToken)
            ? Result<AuthorityGrantSummary>.Success(AuthorityGrantSummary.From(grant, now, forStaff: true))
            : Result<AuthorityGrantSummary>.Failure(AuthorityRequestErrors.GrantNotRevocable);
    }

    public async Task<Result<IReadOnlyList<AuthorityGrantSummary>>> ListGrantsAsync(
        Guid? propertyId, Guid? ownerUserId, CancellationToken cancellationToken = default)
    {
        var staff = await ReaderAsync(cancellationToken);

        if (staff.Error is { } error)
        {
            return Result<IReadOnlyList<AuthorityGrantSummary>>.Failure(error);
        }

        var now = clock.UtcNow;
        var grants = await repository.ListGrantsAsync(propertyId, ownerUserId, cancellationToken);

        return Result<IReadOnlyList<AuthorityGrantSummary>>.Success(
            grants.Select(grant => AuthorityGrantSummary.From(grant, now, forStaff: true)).ToArray());
    }

    // ---- helpers

    /// <summary>
    /// Who the grants name as the owner. An owner is their own verified profile, linked to their account. A
    /// representative's principal is the owner recorded on this request (name and ownership document number), who has
    /// no account here, so the request itself is the legal-party reference and nothing is linked.
    /// </summary>
    private static ListingOwnerIdentity DeclaredOwner(AuthorityRequest request) =>
        request.Capacity == ListingRepresentationKind.Owner
            ? new(ListingOwnerKind.Individual, request.RequesterProfileId, request.RequesterUserId)
            : new(ListingOwnerKind.Individual, request.Id, linkedUserId: null);

    private Task<Result<ListingReviewContext>> IndependentAsync(
        Guid staffUserId, AuthorityRequest request, bool authorized, CancellationToken cancellationToken) =>
        IndependentAsync(staffUserId, request.RequesterUserId, DeclaredOwner(request), authorized, cancellationToken);

    /// <summary>
    /// The staff member's facts from the trusted provider. A known conflict is "not independent"; anything unknown is
    /// "unverifiable". Both are refusals.
    /// </summary>
    private async Task<Result<ListingReviewContext>> IndependentAsync(
        Guid staffUserId, Guid ownerUserId, ListingOwnerIdentity declaredOwner, bool authorized, CancellationToken cancellationToken)
    {
        if (staffUserId == ownerUserId || staffUserId == declaredOwner.LinkedUserId)
        {
            return Result<ListingReviewContext>.Failure(AuthorityRequestErrors.NotIndependent);
        }

        var facts = await independence.GetAsync(staffUserId, ownerUserId, declaredOwner, null, cancellationToken);
        ArgumentNullException.ThrowIfNull(facts);

        if (facts.ReviewerHasOwnerConflict == true)
        {
            return Result<ListingReviewContext>.Failure(AuthorityRequestErrors.NotIndependent);
        }

        var context = new ListingReviewContext(
            staffUserId, ownerUserId, declaredOwner, null, facts.IdentitiesVerified, authorized, null, facts.ReviewerHasOwnerConflict);

        try
        {
            context.EnsureIndependentReviewer();
        }
        catch (InvalidOperationException)
        {
            return Result<ListingReviewContext>.Failure(AuthorityRequestErrors.IndependenceUnverifiable);
        }

        return Result<ListingReviewContext>.Success(context);
    }

    private async Task<Result<AuthorityRequestView>> SaveAsync(AuthorityRequest request, CancellationToken cancellationToken)
    {
        return await repository.TrySaveAsync(cancellationToken)
            ? Result<AuthorityRequestView>.Success(AuthorityRequestView.From(request, forStaff: true))
            : Result<AuthorityRequestView>.Failure(AuthorityRequestErrors.ConcurrentChange);
    }

    private async Task<Result<AuthorityRequest>> LoadForReadAsync(Guid requestId, CancellationToken cancellationToken)
    {
        var staff = await ReaderAsync(cancellationToken);

        if (staff.Error is { } error)
        {
            return Result<AuthorityRequest>.Failure(error);
        }

        var request = requestId == Guid.Empty ? null : await repository.GetAsync(requestId, cancellationToken);

        // Drafts belong to the owner until submitted.
        return request is null || request.Status == AuthorityRequestStatus.Draft
            ? Result<AuthorityRequest>.Failure(AuthorityRequestErrors.NotFound)
            : Result<AuthorityRequest>.Success(request);
    }

    private async Task<ActingWriter> ReaderAsync(CancellationToken cancellationToken)
    {
        var account = await ActingWriter.ResolveAsync(currentUser, users, cancellationToken);

        if (account.Error is not null)
        {
            return account;
        }

        return await HoldsAnyAsync(account.UserId, ReadPermissions, cancellationToken)
            ? account
            : new ActingWriter(Guid.Empty, AuthorityRequestErrors.Forbidden);
    }

    private async Task<ActingWriter> DeciderAsync(string permissionCode, CancellationToken cancellationToken)
    {
        var account = await ActingWriter.ResolveAsync(currentUser, users, cancellationToken);

        if (account.Error is not null)
        {
            return account;
        }

        return await HoldsAsync(account.UserId, permissionCode, cancellationToken)
            ? account
            : new ActingWriter(Guid.Empty, AuthorityRequestErrors.Forbidden);
    }

    private async Task<bool> HoldsAnyAsync(Guid userId, IEnumerable<string> codes, CancellationToken cancellationToken)
    {
        foreach (var code in codes)
        {
            if (await HoldsAsync(userId, code, cancellationToken))
            {
                return true;
            }
        }

        return false;
    }

    private async Task<bool> HoldsAsync(Guid userId, string code, CancellationToken cancellationToken)
    {
        var decision = await permissions.CheckAsync(
            AuthorizationRequest.For(userId, code, AuthorizationTarget.Platform), cancellationToken);

        return decision.IsAllowed;
    }

    private static string Describe(ArgumentException exception)
    {
        var message = exception.Message;
        var index = message.IndexOf(" (Parameter", StringComparison.Ordinal);

        return index >= 0 ? message[..index] : message;
    }
}
