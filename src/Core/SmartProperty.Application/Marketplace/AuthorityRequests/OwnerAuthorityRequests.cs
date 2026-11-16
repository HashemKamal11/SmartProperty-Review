using System.Security.Cryptography;
using SmartProperty.Application.Abstractions.Identity;
using SmartProperty.Application.Abstractions.Persistence.Repositories;
using SmartProperty.Application.Abstractions.Storage;
using SmartProperty.Application.Abstractions.Time;
using SmartProperty.Application.Marketplace.ListingSubmissions.Write;
using SmartProperty.Application.PropertyImages;
using SmartProperty.Common.Results;
using SmartProperty.Domain.Marketplace;
using SmartProperty.Domain.OwnerVerification;
using SmartProperty.Domain.PropertyRegistry;

namespace SmartProperty.Application.Marketplace.AuthorityRequests;

/// <summary>One file being attached. The stream must be readable from its start and seekable; the caller disposes it.</summary>
public sealed record AuthorityDocumentUpload(string? DocumentType, string? FileName, Stream Content, long Length);

/// <summary>
/// The capacity and details of a request. <c>Capacity</c> is <c>Owner</c> or <c>IndividualRepresentative</c>; the
/// ownership document number is always required; the brokerage license number, agency name and owner's name are
/// required for a representative and ignored for an owner.
/// </summary>
public sealed record AuthorityRequestDetailsInput(
    string? Capacity,
    string? OwnershipDocumentNumber,
    string? BrokerageLicenseNumber,
    string? AgencyName,
    string? PropertyOwnerName);

/// <summary>
/// What a requester does with their own authority requests: start one for a property they registered (as its owner
/// or as the owner's authorized representative), attach documents, submit, withdraw, and read their requests and grants.
/// </summary>
/// <remarks>
/// The signed-in user is read from <see cref="ICurrentUser"/> only and must be Active. A request can be started only
/// by the user who registered the property, while their owner profile (their verified identity) is approved, for
/// purposes they do not already hold a grant for, and when the property has no other open request. Another user's
/// request is reported as not found, so its existence is not revealed.
/// </remarks>
public sealed class OwnerAuthorityRequests(
    ICurrentUser currentUser,
    IUserRepository users,
    IPropertyRepository properties,
    IOwnerProfileRepository ownerProfiles,
    IAuthorityRequestRepository repository,
    IListingSubmissionWriteRepository listings,
    IPrivateDocumentStorage storage,
    IDateTimeProvider clock)
{
    public async Task<Result<AuthorityRequestView>> CreateAsync(
        Guid propertyId,
        IReadOnlyList<string>? purposes,
        AuthorityRequestDetailsInput? details,
        CancellationToken cancellationToken = default)
    {
        var owner = await ActingWriter.ResolveAsync(currentUser, users, cancellationToken);

        if (owner.Error is { } accountError)
        {
            return Result<AuthorityRequestView>.Failure(accountError);
        }

        if (!TryParsePurposes(purposes, out var sale, out var rent))
        {
            return Result<AuthorityRequestView>.Failure(AuthorityRequestErrors.Validation(
                "Name the purposes to request: \"Sale\", \"Rent\", or both, each once."));
        }

        // The capacity is chosen later in the wizard; when it is already given, it must be valid.
        var capacity = default(ListingRepresentationKind);

        if (details?.Capacity is not null && !TryParseCapacity(details.Capacity, out capacity))
        {
            return Result<AuthorityRequestView>.Failure(AuthorityRequestErrors.Validation(
                "capacity must be Owner or IndividualRepresentative."));
        }

        var property = propertyId == Guid.Empty ? null : await properties.GetByIdAsync(propertyId, cancellationToken);

        if (property is null)
        {
            return Result<AuthorityRequestView>.Failure(AuthorityRequestErrors.PropertyNotFound);
        }

        if (property.OwnerUserId != owner.UserId)
        {
            return Result<AuthorityRequestView>.Failure(AuthorityRequestErrors.NotPropertyOwner);
        }

        if (property.Status != PropertyStatus.Active)
        {
            return Result<AuthorityRequestView>.Failure(AuthorityRequestErrors.PropertyNotActive);
        }

        var profile = await ownerProfiles.GetByUserIdAsync(owner.UserId, cancellationToken);

        if (profile is null || profile.Status != OwnerVerificationStatus.Approved)
        {
            return Result<AuthorityRequestView>.Failure(AuthorityRequestErrors.OwnerNotApproved);
        }

        // Sold through the platform: the seller no longer advertises it; only the buyer's ownership transfer reopens it.
        if (await repository.IsAwaitingOwnershipTransferAsync(propertyId, cancellationToken))
        {
            return Result<AuthorityRequestView>.Failure(AuthorityRequestErrors.SoldAwaitingTransfer);
        }

        if (await repository.HasOpenRequestAsync(propertyId, cancellationToken))
        {
            return Result<AuthorityRequestView>.Failure(AuthorityRequestErrors.AlreadyOpen);
        }

        var now = clock.UtcNow;
        var grantError = await GrantObstacleAsync(repository, owner.UserId, propertyId, sale, rent, now, cancellationToken);

        if (grantError is not null)
        {
            return Result<AuthorityRequestView>.Failure(grantError);
        }

        var request = new AuthorityRequest(Guid.NewGuid(), propertyId, owner.UserId, profile.Id, sale, rent, now);

        return await SaveNewAsync(request, details, capacity, cancellationToken);
    }

    /// <summary>
    /// Starts a request to take over the ownership of a property registered to someone else: the caller bought it, on
    /// the platform or outside it, and proves it with ownership documents. It goes through the same wizard and the same
    /// two reviews; on approval the property is registered to the caller and advertised from the same listing, so its
    /// history stays with it.
    /// </summary>
    /// <remarks>
    /// The caller needs an approved owner profile and must not already own the property. A platform sale to the caller
    /// is linked automatically. Refused while a sale or rental is in force. One open claim per caller and property; a
    /// claim never blocks the owner's own requests.
    /// </remarks>
    public async Task<Result<AuthorityRequestView>> CreateOwnershipTransferAsync(
        Guid propertyId,
        IReadOnlyList<string>? purposes,
        AuthorityRequestDetailsInput? details,
        CancellationToken cancellationToken = default)
    {
        var claimant = await ActingWriter.ResolveAsync(currentUser, users, cancellationToken);

        if (claimant.Error is { } accountError)
        {
            return Result<AuthorityRequestView>.Failure(accountError);
        }

        if (!TryParsePurposes(purposes, out var sale, out var rent))
        {
            return Result<AuthorityRequestView>.Failure(AuthorityRequestErrors.Validation(
                "Name the purposes to request: \"Sale\", \"Rent\", or both, each once."));
        }

        var capacity = default(ListingRepresentationKind);

        if (details?.Capacity is not null && !TryParseCapacity(details.Capacity, out capacity))
        {
            return Result<AuthorityRequestView>.Failure(AuthorityRequestErrors.Validation(
                "capacity must be Owner or IndividualRepresentative."));
        }

        var property = propertyId == Guid.Empty ? null : await properties.GetByIdAsync(propertyId, cancellationToken);

        if (property is null)
        {
            return Result<AuthorityRequestView>.Failure(AuthorityRequestErrors.PropertyNotFound);
        }

        if (property.OwnerUserId == claimant.UserId)
        {
            return Result<AuthorityRequestView>.Failure(AuthorityRequestErrors.AlreadyPropertyOwner);
        }

        if (property.Status != PropertyStatus.Active)
        {
            return Result<AuthorityRequestView>.Failure(AuthorityRequestErrors.PropertyNotActive);
        }

        var profile = await ownerProfiles.GetByUserIdAsync(claimant.UserId, cancellationToken);

        if (profile is null || profile.Status != OwnerVerificationStatus.Approved)
        {
            return Result<AuthorityRequestView>.Failure(AuthorityRequestErrors.OwnerNotApproved);
        }

        var now = clock.UtcNow;

        if (await repository.HasCommitmentInForceAsync(propertyId, now, cancellationToken))
        {
            return Result<AuthorityRequestView>.Failure(AuthorityRequestErrors.CommitmentInForce);
        }

        if (await repository.HasOpenTransferRequestAsync(propertyId, claimant.UserId, cancellationToken))
        {
            return Result<AuthorityRequestView>.Failure(AuthorityRequestErrors.TransferAlreadyOpen);
        }

        var platformSaleId = await repository.FindUntransferredSaleAsync(propertyId, claimant.UserId, cancellationToken);
        var request = AuthorityRequest.ForOwnershipTransfer(
            Guid.NewGuid(), propertyId, claimant.UserId, profile.Id, sale, rent, property.OwnerUserId, platformSaleId, now);

        return await SaveNewAsync(request, details, capacity, cancellationToken);
    }

    /// <summary>
    /// The history of a property the caller owns now: its ownership changes and every version that was published,
    /// whoever published it. Public facts only; a property the caller does not own is not found.
    /// </summary>
    public async Task<Result<PropertyHistoryView>> GetPropertyHistoryAsync(Guid propertyId, CancellationToken cancellationToken = default)
    {
        var caller = await ActingWriter.ResolveAsync(currentUser, users, cancellationToken);

        if (caller.Error is { } accountError)
        {
            return Result<PropertyHistoryView>.Failure(accountError);
        }

        var property = propertyId == Guid.Empty ? null : await properties.GetByIdAsync(propertyId, cancellationToken);

        if (property is null || property.OwnerUserId != caller.UserId)
        {
            return Result<PropertyHistoryView>.Failure(AuthorityRequestErrors.PropertyNotFound);
        }

        var transfers = await repository.ListOwnershipTransfersAsync(propertyId, cancellationToken);
        var versions = await repository.ListPublishedVersionsAsync(propertyId, cancellationToken);

        return Result<PropertyHistoryView>.Success(new PropertyHistoryView(
            propertyId,
            transfers
                .Select(transfer => new OwnershipChangeView(
                    transfer.TransferredAt, transfer.NewOwnerUserId == caller.UserId, transfer.SaleCommitmentId is not null))
                .ToArray(),
            versions
                .Select(version => new PublishedListingVersionView(
                    version.ListingId,
                    version.Purpose.ToString(),
                    version.Version,
                    version.Status.ToString(),
                    version.ReviewDecision?.DecidedAt,
                    version.Content.Title,
                    version.Content.Description,
                    version.Content.Amount,
                    version.Content.Currency,
                    version.Content.RentPeriod?.ToString(),
                    version.Authority.ActingUserId == caller.UserId))
                .ToArray()));
    }

    private async Task<Result<AuthorityRequestView>> SaveNewAsync(
        AuthorityRequest request, AuthorityRequestDetailsInput? details, ListingRepresentationKind capacity, CancellationToken cancellationToken)
    {
        var now = request.CreatedAt;

        if (details?.Capacity is not null)
        {
            try
            {
                request.UpdateDetails(capacity, details.OwnershipDocumentNumber!, Representative(details), now);
            }
            catch (ArgumentException exception)
            {
                return Result<AuthorityRequestView>.Failure(AuthorityRequestErrors.Validation(Describe(exception)));
            }
        }

        repository.Add(request);

        return await repository.TrySaveAsync(cancellationToken)
            ? Result<AuthorityRequestView>.Success(AuthorityRequestView.From(request, forStaff: false))
            : Result<AuthorityRequestView>.Failure(request.IsOwnershipTransfer ? AuthorityRequestErrors.TransferAlreadyOpen : AuthorityRequestErrors.AlreadyOpen);
    }

    /// <summary>Changes the capacity and details of a draft. Documents already attached stay.</summary>
    public async Task<Result<AuthorityRequestView>> UpdateDetailsAsync(
        Guid requestId, AuthorityRequestDetailsInput? details, CancellationToken cancellationToken = default)
    {
        var loaded = await LoadOwnAsync(requestId, cancellationToken);

        if (loaded.IsFailure)
        {
            return Result<AuthorityRequestView>.Failure(loaded.Error!);
        }

        var request = loaded.Value;

        if (request.Status != AuthorityRequestStatus.Draft)
        {
            return Result<AuthorityRequestView>.Failure(AuthorityRequestErrors.NotEditable);
        }

        if (!TryParseCapacity(details?.Capacity, out var capacity))
        {
            return Result<AuthorityRequestView>.Failure(AuthorityRequestErrors.Validation(
                "capacity must be Owner or IndividualRepresentative."));
        }

        try
        {
            request.UpdateDetails(capacity, details!.OwnershipDocumentNumber!, Representative(details), clock.UtcNow);
        }
        catch (ArgumentException exception)
        {
            return Result<AuthorityRequestView>.Failure(AuthorityRequestErrors.Validation(Describe(exception)));
        }

        return await repository.TrySaveAsync(cancellationToken)
            ? Result<AuthorityRequestView>.Success(AuthorityRequestView.From(request, forStaff: false))
            : Result<AuthorityRequestView>.Failure(AuthorityRequestErrors.ConcurrentChange);
    }

    /// <summary>
    /// Saves the price and terms for one requested purpose (step 3 of the wizard), validated now against the property
    /// exactly as a listing draft would be. The listing itself is created only on the final approval.
    /// </summary>
    public async Task<Result<AuthorityRequestView>> SetListingAsync(
        Guid requestId, string? purposeText, ListingDraftContentInput? content, CancellationToken cancellationToken = default)
    {
        var loaded = await LoadOwnAsync(requestId, cancellationToken);

        if (loaded.IsFailure)
        {
            return Result<AuthorityRequestView>.Failure(loaded.Error!);
        }

        var request = loaded.Value;

        if (!TryParseName(purposeText, out ListingPurpose purpose) || !request.Purposes.Contains(purpose))
        {
            return Result<AuthorityRequestView>.Failure(AuthorityRequestErrors.PurposeNotRequested);
        }

        if (request.Status != AuthorityRequestStatus.Draft)
        {
            return Result<AuthorityRequestView>.Failure(AuthorityRequestErrors.NotEditable);
        }

        if (ListingDraftContentValidator.Validate(content, purpose) is { } invalid)
        {
            return Result<AuthorityRequestView>.Failure(invalid);
        }

        // The same canonical build a listing draft goes through: the content must fit this property as it is now. The
        // lock opens the transaction that the commit below closes.
        var property = await listings.LockPropertyAsync(request.PropertyId, cancellationToken);

        if (property is null || property.PropertyId != request.PropertyId)
        {
            return Result<AuthorityRequestView>.Failure(AuthorityRequestErrors.PropertyNotFound);
        }

        var built = CanonicalListingContent.Build(property, purpose, content!);

        if (built.IsFailure)
        {
            return Result<AuthorityRequestView>.Failure(built.Error!);
        }

        // Evidence is never taken from the client here: the request's own scanned documents become the listing's
        // evidence on the final approval.
        request.SetListingContent(purpose, AuthorityListingContent.Serialize(content! with { EvidenceIds = [] }), clock.UtcNow);

        if (await WriteCommit.SaveAsync(listings, WriteCommit.ForChange, cancellationToken) is not null)
        {
            return Result<AuthorityRequestView>.Failure(AuthorityRequestErrors.ConcurrentChange);
        }

        return Result<AuthorityRequestView>.Success(AuthorityRequestView.From(request, forStaff: false));
    }

    /// <summary>
    /// Attaches a new document to a draft. The bytes are checked (size, real type from the content), stored privately
    /// under a generated name in quarantine, and queued for the malware scan. Returns the document with
    /// <c>scanStatus</c> PendingScan.
    /// </summary>
    public async Task<Result<AuthorityDocumentView>> AddDocumentAsync(
        Guid requestId, AuthorityDocumentUpload upload, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(upload);

        var loaded = await LoadOwnAsync(requestId, cancellationToken);

        if (loaded.IsFailure)
        {
            return Result<AuthorityDocumentView>.Failure(loaded.Error!);
        }

        var request = loaded.Value;

        if (!TryParseName(upload.DocumentType, out AuthorityDocumentType type))
        {
            return Result<AuthorityDocumentView>.Failure(AuthorityRequestErrors.Validation(
                "documentType must be TitleDeed, SaleContract, Authorization, BrokerageLicense, UtilityBill or Other."));
        }

        if (request.Status != AuthorityRequestStatus.Draft)
        {
            return Result<AuthorityDocumentView>.Failure(AuthorityRequestErrors.NotEditable);
        }

        if (request.CurrentDocuments.Count >= AuthorityRequest.MaxDocuments)
        {
            return Result<AuthorityDocumentView>.Failure(AuthorityRequestErrors.Validation(
                $"A request may carry at most {AuthorityRequest.MaxDocuments} documents."));
        }

        return await StoreVersionAsync(request, Guid.NewGuid(), 1, type, upload, replace: false, cancellationToken);
    }

    /// <summary>
    /// Attaches several new documents to a draft in one go: all of them or none. Each file gets the same checks as
    /// <see cref="AddDocumentAsync"/> and is quarantined pending its own malware scan. <paramref name="documentTypes"/>
    /// names the type of each file in the same order, or holds one type that applies to every file.
    /// </summary>
    public async Task<Result<IReadOnlyList<AuthorityDocumentView>>> AddDocumentsAsync(
        Guid requestId,
        IReadOnlyList<AuthorityDocumentUpload> uploads,
        IReadOnlyList<string?> documentTypes,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(uploads);
        ArgumentNullException.ThrowIfNull(documentTypes);

        var loaded = await LoadOwnAsync(requestId, cancellationToken);

        if (loaded.IsFailure)
        {
            return Result<IReadOnlyList<AuthorityDocumentView>>.Failure(loaded.Error!);
        }

        var request = loaded.Value;

        if (uploads.Count == 0)
        {
            return Result<IReadOnlyList<AuthorityDocumentView>>.Failure(AuthorityRequestErrors.Validation(
                "At least one file is required in the 'files' field."));
        }

        if (documentTypes.Count != uploads.Count && documentTypes.Count != 1)
        {
            return Result<IReadOnlyList<AuthorityDocumentView>>.Failure(AuthorityRequestErrors.Validation(
                $"Send one 'documentTypes' value per file, in the same order ({uploads.Count} files, {documentTypes.Count} types), or a single value for all of them."));
        }

        var types = new AuthorityDocumentType[uploads.Count];

        for (var index = 0; index < uploads.Count; index++)
        {
            var text = documentTypes.Count == 1 ? documentTypes[0] : documentTypes[index];

            if (!TryParseName(text, out types[index]))
            {
                return Result<IReadOnlyList<AuthorityDocumentView>>.Failure(AuthorityRequestErrors.Validation(
                    $"documentTypes[{index}] must be TitleDeed, SaleContract, Authorization, BrokerageLicense, UtilityBill or Other."));
            }
        }

        if (request.Status != AuthorityRequestStatus.Draft)
        {
            return Result<IReadOnlyList<AuthorityDocumentView>>.Failure(AuthorityRequestErrors.NotEditable);
        }

        if (request.CurrentDocuments.Count + uploads.Count > AuthorityRequest.MaxDocuments)
        {
            return Result<IReadOnlyList<AuthorityDocumentView>>.Failure(AuthorityRequestErrors.Validation(
                $"A request may carry at most {AuthorityRequest.MaxDocuments} documents; it has {request.CurrentDocuments.Count} and {uploads.Count} were sent."));
        }

        // One file at a time is read, checked and quarantined, so only one is held in memory. Any failure removes
        // every file already stored by this call, so nothing is left behind unrecorded.
        var versions = new List<AuthorityRequestDocument>(uploads.Count);
        var now = clock.UtcNow;

        try
        {
            for (var index = 0; index < uploads.Count; index++)
            {
                var prepared = await PrepareVersionAsync(request, Guid.NewGuid(), 1, types[index], uploads[index], now, cancellationToken);

                if (prepared.IsFailure)
                {
                    await DeleteQuietlyAsync(versions.Select(version => version.StorageKey));

                    return Result<IReadOnlyList<AuthorityDocumentView>>.Failure(prepared.Error!);
                }

                versions.Add(prepared.Value);
            }

            foreach (var version in versions)
            {
                request.AddDocument(version, now);
            }

            if (!await repository.TrySaveAsync(cancellationToken))
            {
                await DeleteQuietlyAsync(versions.Select(version => version.StorageKey));

                return Result<IReadOnlyList<AuthorityDocumentView>>.Failure(AuthorityRequestErrors.ConcurrentChange);
            }
        }
        catch
        {
            await DeleteQuietlyAsync(versions.Select(version => version.StorageKey));
            throw;
        }

        return Result<IReadOnlyList<AuthorityDocumentView>>.Success(
            versions.Select(version => AuthorityDocumentView.From(request, version, forStaff: false)).ToArray());
    }

    /// <summary>
    /// Replaces the version in force of a draft's document with a new file of the same type. The earlier version and
    /// its bytes are kept, superseded, so what was reviewed before stays on record.
    /// </summary>
    public async Task<Result<AuthorityDocumentView>> ReplaceDocumentAsync(
        Guid requestId, Guid documentId, AuthorityDocumentUpload upload, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(upload);

        var loaded = await LoadOwnAsync(requestId, cancellationToken);

        if (loaded.IsFailure)
        {
            return Result<AuthorityDocumentView>.Failure(loaded.Error!);
        }

        var request = loaded.Value;
        var current = request.CurrentVersionOf(documentId);

        if (current is null)
        {
            return Result<AuthorityDocumentView>.Failure(AuthorityRequestErrors.DocumentNotFound);
        }

        if (request.Status != AuthorityRequestStatus.Draft)
        {
            return Result<AuthorityDocumentView>.Failure(AuthorityRequestErrors.NotEditable);
        }

        if (request.VersionsOf(documentId).Count >= AuthorityRequest.MaxVersionsPerDocument)
        {
            return Result<AuthorityDocumentView>.Failure(AuthorityRequestErrors.TooManyVersions);
        }

        var next = request.VersionsOf(documentId)[^1].VersionNumber + 1;

        return await StoreVersionAsync(request, documentId, next, current.Type, upload, replace: true, cancellationToken);
    }

    /// <summary>
    /// Removes a document from a draft. Its record and every stored version are kept for the history; a document that
    /// was part of an approval cannot be removed (replace it instead).
    /// </summary>
    public async Task<Result<AuthorityRequestView>> RemoveDocumentAsync(
        Guid requestId, Guid documentId, CancellationToken cancellationToken = default)
    {
        var loaded = await LoadOwnAsync(requestId, cancellationToken);

        if (loaded.IsFailure)
        {
            return Result<AuthorityRequestView>.Failure(loaded.Error!);
        }

        var request = loaded.Value;

        if (request.CurrentVersionOf(documentId) is null)
        {
            return Result<AuthorityRequestView>.Failure(AuthorityRequestErrors.DocumentNotFound);
        }

        if (request.Status != AuthorityRequestStatus.Draft)
        {
            return Result<AuthorityRequestView>.Failure(AuthorityRequestErrors.NotEditable);
        }

        if (request.WasApproved(documentId))
        {
            return Result<AuthorityRequestView>.Failure(AuthorityRequestErrors.DocumentLockedByApproval);
        }

        request.RemoveDocument(documentId, clock.UtcNow);

        return await repository.TrySaveAsync(cancellationToken)
            ? Result<AuthorityRequestView>.Success(AuthorityRequestView.From(request, forStaff: false))
            : Result<AuthorityRequestView>.Failure(AuthorityRequestErrors.ConcurrentChange);
    }

    /// <summary>Queues a document whose scan failed for a fresh set of scan attempts. Drafts only.</summary>
    public async Task<Result<AuthorityDocumentView>> RescanDocumentAsync(
        Guid requestId, Guid documentId, CancellationToken cancellationToken = default)
    {
        var loaded = await LoadOwnAsync(requestId, cancellationToken);

        if (loaded.IsFailure)
        {
            return Result<AuthorityDocumentView>.Failure(loaded.Error!);
        }

        var request = loaded.Value;
        var current = request.CurrentVersionOf(documentId);

        if (current is null)
        {
            return Result<AuthorityDocumentView>.Failure(AuthorityRequestErrors.DocumentNotFound);
        }

        if (request.Status != AuthorityRequestStatus.Draft)
        {
            return Result<AuthorityDocumentView>.Failure(AuthorityRequestErrors.NotEditable);
        }

        if (current.ScanStatus != DocumentScanStatus.ScanFailed)
        {
            return Result<AuthorityDocumentView>.Failure(AuthorityRequestErrors.DocumentNotRescannable);
        }

        current.RequestRescan(clock.UtcNow);

        return await repository.TrySaveAsync(cancellationToken)
            ? Result<AuthorityDocumentView>.Success(AuthorityDocumentView.From(request, current, forStaff: false))
            : Result<AuthorityDocumentView>.Failure(AuthorityRequestErrors.ConcurrentChange);
    }

    /// <summary>The request's documents in force, or with <paramref name="includeHistory"/> every version ever stored.</summary>
    public async Task<Result<IReadOnlyList<AuthorityDocumentView>>> ListDocumentsAsync(
        Guid requestId, bool includeHistory, CancellationToken cancellationToken = default)
    {
        var loaded = await LoadOwnAsync(requestId, cancellationToken);

        return loaded.IsFailure
            ? Result<IReadOnlyList<AuthorityDocumentView>>.Failure(loaded.Error!)
            : Result<IReadOnlyList<AuthorityDocumentView>>.Success(DocumentList(loaded.Value, includeHistory, forStaff: false));
    }

    public async Task<Result<AuthorityDocumentDetailView>> GetDocumentAsync(
        Guid requestId, Guid documentId, CancellationToken cancellationToken = default)
    {
        var loaded = await LoadOwnAsync(requestId, cancellationToken);

        if (loaded.IsFailure)
        {
            return Result<AuthorityDocumentDetailView>.Failure(loaded.Error!);
        }

        return loaded.Value.VersionsOf(documentId).Count == 0
            ? Result<AuthorityDocumentDetailView>.Failure(AuthorityRequestErrors.DocumentNotFound)
            : Result<AuthorityDocumentDetailView>.Success(AuthorityDocumentDetailView.From(loaded.Value, documentId, forStaff: false));
    }

    public Task<Result<AuthorityRequestView>> SubmitAsync(Guid requestId, CancellationToken cancellationToken = default)
    {
        return TransitionAsync(requestId, request =>
        {
            if (request.Status != AuthorityRequestStatus.Draft)
            {
                return AuthorityRequestErrors.InvalidState;
            }

            if (request.Missing.Count > 0)
            {
                return AuthorityRequestErrors.Incomplete(request.Missing);
            }

            request.Submit(clock.UtcNow);

            return null;
        }, cancellationToken);
    }

    public Task<Result<AuthorityRequestView>> WithdrawAsync(Guid requestId, CancellationToken cancellationToken = default)
    {
        return TransitionAsync(requestId, request =>
        {
            if (!request.IsOpen)
            {
                return AuthorityRequestErrors.InvalidState;
            }

            request.Withdraw(clock.UtcNow);

            return null;
        }, cancellationToken);
    }

    public async Task<Result<IReadOnlyList<AuthorityRequestView>>> ListAsync(
        Guid? propertyId, CancellationToken cancellationToken = default)
    {
        var owner = await ActingWriter.ResolveAsync(currentUser, users, cancellationToken);

        if (owner.Error is { } accountError)
        {
            return Result<IReadOnlyList<AuthorityRequestView>>.Failure(accountError);
        }

        var requests = await repository.ListForOwnerAsync(owner.UserId, propertyId, cancellationToken);

        return Result<IReadOnlyList<AuthorityRequestView>>.Success(
            requests.Select(request => AuthorityRequestView.From(request, forStaff: false)).ToArray());
    }

    public async Task<Result<AuthorityRequestView>> GetAsync(Guid requestId, CancellationToken cancellationToken = default)
    {
        var loaded = await LoadOwnAsync(requestId, cancellationToken);

        return loaded.IsFailure
            ? Result<AuthorityRequestView>.Failure(loaded.Error!)
            : Result<AuthorityRequestView>.Success(AuthorityRequestView.From(loaded.Value, forStaff: false));
    }

    /// <summary>
    /// Streams a document: its version in force, or the version named. Only a version the scanner marked Clean is
    /// served; anything still in quarantine, infected or unscanned is refused.
    /// </summary>
    public async Task<Result<AuthorityDocumentFile>> OpenDocumentAsync(
        Guid requestId, Guid documentId, Guid? versionId = null, CancellationToken cancellationToken = default)
    {
        var loaded = await LoadOwnAsync(requestId, cancellationToken);

        if (loaded.IsFailure)
        {
            return Result<AuthorityDocumentFile>.Failure(loaded.Error!);
        }

        return await OpenAsync(storage, loaded.Value, documentId, versionId, cancellationToken);
    }

    /// <summary>The signed-in user's grants, newest first. Staff user ids are not included.</summary>
    public async Task<Result<IReadOnlyList<AuthorityGrantSummary>>> ListGrantsAsync(
        Guid? propertyId, CancellationToken cancellationToken = default)
    {
        var owner = await ActingWriter.ResolveAsync(currentUser, users, cancellationToken);

        if (owner.Error is { } accountError)
        {
            return Result<IReadOnlyList<AuthorityGrantSummary>>.Failure(accountError);
        }

        var now = clock.UtcNow;
        var grants = await repository.ListGrantsAsync(propertyId, owner.UserId, cancellationToken);

        return Result<IReadOnlyList<AuthorityGrantSummary>>.Success(
            grants.Select(grant => AuthorityGrantSummary.From(grant, now, forStaff: false)).ToArray());
    }

    // ---- shared with the staff side

    /// <summary>Why a grant cannot be added for these purposes, or null: one still in force, or an expired one never revoked.</summary>
    internal static async Task<Common.Results.Error?> GrantObstacleAsync(
        IAuthorityRequestRepository repository, Guid ownerUserId, Guid propertyId, bool sale, bool rent,
        DateTimeOffset now, CancellationToken cancellationToken)
    {
        var unrevoked = await repository.GetUnrevokedGrantsAsync(ownerUserId, propertyId, cancellationToken);

        foreach (var grant in unrevoked.Where(grant => (grant.Purpose == ListingPurpose.Sale && sale) || (grant.Purpose == ListingPurpose.Rent && rent)))
        {
            return grant.Authority.ValidUntil is { } until && now >= until
                ? AuthorityRequestErrors.ExpiredGrantNotRevoked
                : AuthorityRequestErrors.AlreadyGranted;
        }

        return null;
    }

    internal static IReadOnlyList<AuthorityDocumentView> DocumentList(AuthorityRequest request, bool includeHistory, bool forStaff)
    {
        var versions = includeHistory
            ? request.Documents.OrderBy(document => document.UploadedAt).ThenBy(document => document.VersionNumber).ToArray()
            : request.CurrentDocuments;

        return versions.Select(version => AuthorityDocumentView.From(request, version, forStaff)).ToArray();
    }

    internal static async Task<Result<AuthorityDocumentFile>> OpenAsync(
        IPrivateDocumentStorage storage, AuthorityRequest request, Guid documentId, Guid? versionId, CancellationToken cancellationToken)
    {
        var document = versionId is { } version
            ? request.Documents.SingleOrDefault(candidate => candidate.Id == version && candidate.DocumentId == documentId)
            : request.CurrentVersionOf(documentId);

        if (document is null)
        {
            return Result<AuthorityDocumentFile>.Failure(AuthorityRequestErrors.DocumentNotFound);
        }

        // Quarantined, infected and unscanned bytes are never served.
        if (!document.IsAvailable)
        {
            return Result<AuthorityDocumentFile>.Failure(AuthorityRequestErrors.DocumentNotAvailable);
        }

        var stream = await storage.OpenReadAsync(document.StorageKey, cancellationToken);

        return stream is null
            ? Result<AuthorityDocumentFile>.Failure(AuthorityRequestErrors.DocumentNotFound)
            : Result<AuthorityDocumentFile>.Success(new AuthorityDocumentFile(stream, document.ContentType, DownloadName(document)));
    }

    private static string DownloadName(AuthorityRequestDocument document)
    {
        var extension = document.ContentType == AuthorityRequestDocument.Pdf ? ".pdf" : PropertyImage.ExtensionOf(document.ContentType);

        return document.FileName ?? $"{document.Type}-v{document.VersionNumber}{extension}";
    }

    private async Task<Result<AuthorityDocumentView>> StoreVersionAsync(
        AuthorityRequest request,
        Guid documentId,
        int versionNumber,
        AuthorityDocumentType type,
        AuthorityDocumentUpload upload,
        bool replace,
        CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;
        var prepared = await PrepareVersionAsync(request, documentId, versionNumber, type, upload, now, cancellationToken);

        if (prepared.IsFailure)
        {
            return Result<AuthorityDocumentView>.Failure(prepared.Error!);
        }

        var version = prepared.Value;
        var key = version.StorageKey;

        try
        {
            if (replace)
            {
                request.ReplaceDocument(version, now);
            }
            else
            {
                request.AddDocument(version, now);
            }

            if (!await repository.TrySaveAsync(cancellationToken))
            {
                await DeleteQuietlyAsync(key);

                return Result<AuthorityDocumentView>.Failure(AuthorityRequestErrors.ConcurrentChange);
            }
        }
        catch
        {
            // Recorded nowhere, so the file must not stay behind.
            await DeleteQuietlyAsync(key);
            throw;
        }

        return Result<AuthorityDocumentView>.Success(AuthorityDocumentView.From(request, version, forStaff: false));
    }

    /// <summary>
    /// Reads and checks one upload, stores it privately under a generated name in quarantine, and returns the version
    /// to record (not yet added to the request). On failure nothing is stored.
    /// </summary>
    private async Task<Result<AuthorityRequestDocument>> PrepareVersionAsync(
        AuthorityRequest request,
        Guid documentId,
        int versionNumber,
        AuthorityDocumentType type,
        AuthorityDocumentUpload upload,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var read = await ReadUploadAsync(upload, cancellationToken);

        if (read.IsFailure)
        {
            return Result<AuthorityRequestDocument>.Failure(read.Error!);
        }

        var (bytes, contentType) = read.Value;
        var versionId = Guid.NewGuid();
        var extension = contentType == AuthorityRequestDocument.Pdf ? ".pdf" : PropertyImage.ExtensionOf(contentType);

        // A generated name in the quarantine area; the client's name is kept only as a label.
        var key = $"{AuthorityDocumentScanProcessor.QuarantinePrefix}{request.Id:N}/{versionId:N}{extension}";
        var version = new AuthorityRequestDocument(
            versionId, request.Id, documentId, versionNumber, type, key, contentType, upload.FileName, bytes.LongLength,
            Convert.ToHexString(SHA256.HashData(bytes)), request.RequesterUserId, now);

        try
        {
            using var content = new MemoryStream(bytes, writable: false);
            await storage.SaveAsync(key, content, cancellationToken);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // A partly written file must not stay behind.
            await DeleteQuietlyAsync(key);

            return Result<AuthorityRequestDocument>.Failure(AuthorityRequestErrors.StorageUnavailable);
        }

        return Result<AuthorityRequestDocument>.Success(version);
    }

    internal static bool TryParseName<TEnum>(string? text, out TEnum value)
        where TEnum : struct, Enum
    {
        value = default;

        // Names only: a number never selects a member.
        return !string.IsNullOrWhiteSpace(text)
            && !char.IsDigit(text.Trim()[0])
            && Enum.TryParse(text.Trim(), ignoreCase: true, out value)
            && Enum.IsDefined(value);
    }

    private static bool TryParseCapacity(string? text, out ListingRepresentationKind capacity)
    {
        return TryParseName(text, out capacity)
            && capacity is ListingRepresentationKind.Owner or ListingRepresentationKind.IndividualRepresentative;
    }

    private static AuthorityRepresentative? Representative(AuthorityRequestDetailsInput details)
    {
        // Ignored for an owner; for a representative the Domain requires all three.
        return details.BrokerageLicenseNumber is null && details.AgencyName is null && details.PropertyOwnerName is null
            ? null
            : new AuthorityRepresentative(
                details.BrokerageLicenseNumber ?? string.Empty, details.AgencyName ?? string.Empty, details.PropertyOwnerName ?? string.Empty);
    }

    internal static string Describe(ArgumentException exception)
    {
        var message = exception.Message;
        var index = message.IndexOf(" (Parameter", StringComparison.Ordinal);

        return index >= 0 ? message[..index] : message;
    }

    private static bool TryParsePurposes(IReadOnlyList<string>? purposes, out bool sale, out bool rent)
    {
        sale = false;
        rent = false;

        if (purposes is null || purposes.Count is 0 or > 2)
        {
            return false;
        }

        foreach (var text in purposes)
        {
            if (!TryParseName(text, out ListingPurpose purpose))
            {
                return false;
            }

            if (purpose == ListingPurpose.Sale)
            {
                if (sale)
                {
                    return false;
                }

                sale = true;
            }
            else
            {
                if (rent)
                {
                    return false;
                }

                rent = true;
            }
        }

        return true;
    }

    private async Task<Result<AuthorityRequestView>> TransitionAsync(
        Guid requestId, Func<AuthorityRequest, Common.Results.Error?> change, CancellationToken cancellationToken)
    {
        var loaded = await LoadOwnAsync(requestId, cancellationToken);

        if (loaded.IsFailure)
        {
            return Result<AuthorityRequestView>.Failure(loaded.Error!);
        }

        if (change(loaded.Value) is { } error)
        {
            return Result<AuthorityRequestView>.Failure(error);
        }

        return await repository.TrySaveAsync(cancellationToken)
            ? Result<AuthorityRequestView>.Success(AuthorityRequestView.From(loaded.Value, forStaff: false))
            : Result<AuthorityRequestView>.Failure(AuthorityRequestErrors.ConcurrentChange);
    }

    private async Task<Result<AuthorityRequest>> LoadOwnAsync(Guid requestId, CancellationToken cancellationToken)
    {
        var owner = await ActingWriter.ResolveAsync(currentUser, users, cancellationToken);

        if (owner.Error is { } accountError)
        {
            return Result<AuthorityRequest>.Failure(accountError);
        }

        var request = requestId == Guid.Empty ? null : await repository.GetAsync(requestId, cancellationToken);

        // Someone else's request is not found, so its existence is not revealed.
        return request is null || request.RequesterUserId != owner.UserId
            ? Result<AuthorityRequest>.Failure(AuthorityRequestErrors.NotFound)
            : Result<AuthorityRequest>.Success(request);
    }

    /// <summary>Reads the whole upload (at most the limit), and detects its real type from its bytes.</summary>
    private static async Task<Result<(byte[] Bytes, string ContentType)>> ReadUploadAsync(
        AuthorityDocumentUpload upload, CancellationToken cancellationToken)
    {
        var label = string.IsNullOrWhiteSpace(upload.FileName) ? "The file" : $"'{Path.GetFileName(upload.FileName)}'";

        if (!upload.Content.CanRead)
        {
            return Result<(byte[], string)>.Failure(AuthorityRequestErrors.Validation($"{label} could not be read."));
        }

        if (upload.Length <= 0)
        {
            return Result<(byte[], string)>.Failure(AuthorityRequestErrors.Validation($"{label} is empty."));
        }

        if (upload.Length > AuthorityRequest.MaxDocumentBytes)
        {
            return Result<(byte[], string)>.Failure(AuthorityRequestErrors.Validation(
                $"{label} is larger than {AuthorityRequest.MaxDocumentBytes / (1024 * 1024)} MB."));
        }

        if (upload.Content.CanSeek)
        {
            upload.Content.Seek(0, SeekOrigin.Begin);
        }

        // Never trust the declared length: read at most one byte past the limit.
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int count;

        while ((count = await upload.Content.ReadAsync(chunk, cancellationToken)) > 0)
        {
            buffer.Write(chunk, 0, count);

            if (buffer.Length > AuthorityRequest.MaxDocumentBytes)
            {
                return Result<(byte[], string)>.Failure(AuthorityRequestErrors.Validation(
                    $"{label} is larger than {AuthorityRequest.MaxDocumentBytes / (1024 * 1024)} MB."));
            }
        }

        var bytes = buffer.ToArray();

        if (bytes.Length == 0)
        {
            return Result<(byte[], string)>.Failure(AuthorityRequestErrors.Validation($"{label} is empty."));
        }

        // The type comes from the bytes, never from the name or the declared type.
        var span = bytes.AsSpan();
        var type = span.StartsWith("%PDF-"u8) ? AuthorityRequestDocument.Pdf : ImageContentSniffer.Detect(span[..Math.Min(span.Length, ImageContentSniffer.HeaderLength)]);

        return type is null
            ? Result<(byte[], string)>.Failure(AuthorityRequestErrors.Validation(
                $"{label} is not a supported document. Only PDF, JPEG, PNG and WebP files are accepted."))
            : Result<(byte[], string)>.Success((bytes, type));
    }

    private async Task DeleteQuietlyAsync(string key)
    {
        try
        {
            await storage.DeleteAsync(key, CancellationToken.None);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Best effort; the outcome already reported is the one that matters.
        }
    }

    private async Task DeleteQuietlyAsync(IEnumerable<string> keys)
    {
        foreach (var key in keys.ToArray())
        {
            await DeleteQuietlyAsync(key);
        }
    }
}
