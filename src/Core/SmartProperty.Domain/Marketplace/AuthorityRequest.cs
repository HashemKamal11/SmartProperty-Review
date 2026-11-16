namespace SmartProperty.Domain.Marketplace;

/// <summary>Where a request to advertise a property stands.</summary>
public enum AuthorityRequestStatus
{
    /// <summary>
    /// The requester is filling it in: listing content, capacity and details, documents. A request a reviewer asked to
    /// complete is a draft again, with the reason on it.
    /// </summary>
    Draft = 1,

    /// <summary>Submitted; a legal reviewer checks the details, the property data and the documents.</summary>
    PendingLegalReview = 2,

    /// <summary>The legal reviewer approved it; a platform administrator (a different person) decides.</summary>
    PendingIssuance = 3,

    /// <summary>The administrator approved it: the grants are issued and the listings are published.</summary>
    Approved = 4,

    /// <summary>A reviewer refused it for a stated reason. Final; the requester may start a new request.</summary>
    Rejected = 5,

    /// <summary>The requester withdrew it before the final decision.</summary>
    Withdrawn = 6
}

/// <summary>Where the malware scan of one stored document version stands. Only <see cref="Clean"/> bytes are ever served.</summary>
public enum DocumentScanStatus
{
    /// <summary>Stored in quarantine; the scanner has not given a verdict yet (a retry may be scheduled).</summary>
    PendingScan = 1,

    /// <summary>The scanner examined these exact bytes and found nothing.</summary>
    Clean = 2,

    /// <summary>The scanner found malware. The bytes are deleted; the record stays.</summary>
    Rejected = 3,

    /// <summary>No verdict after every allowed attempt. Not clean; the requester can ask for a rescan or replace it.</summary>
    ScanFailed = 4
}

/// <summary>One recorded step in the life of a request. Appended, never changed.</summary>
public enum AuthorityDecisionKind
{
    Submitted = 1,
    LegalApproved = 2,
    LegalCompletionRequested = 3,
    LegalRejected = 4,
    AdminApproved = 5,
    AdminCompletionRequested = 6,
    AdminRejected = 7,
    Withdrawn = 8,

    /// <summary>Closed because another request transferred the ownership of the property. Recorded by the issuer.</summary>
    ClosedOwnershipTransferred = 9
}

/// <summary>What a request is for.</summary>
public enum AuthorityRequestKind
{
    /// <summary>The property's registered owner (or their representative) asks to advertise it.</summary>
    Listing = 1,

    /// <summary>
    /// Someone who acquired the property asks to become its owner here and advertise it. On approval the property is
    /// registered to the requester, everything that rested on the previous ownership is closed, and the property keeps
    /// its history.
    /// </summary>
    OwnershipTransfer = 2
}

/// <summary>
/// What one uploaded document is. Everyone needs a <see cref="TitleDeed"/> or a <see cref="SaleContract"/>; an
/// authorized representative also needs an <see cref="Authorization"/> and a <see cref="BrokerageLicense"/>.
/// </summary>
public enum AuthorityDocumentType
{
    /// <summary>The registered title deed or ownership certificate (Saudi deed, Egyptian registered contract, UAE title deed).</summary>
    TitleDeed = 1,

    /// <summary>The contract under which the owner bought the property (for example a preliminary sale contract).</summary>
    SaleContract = 2,

    /// <summary>A recent electricity, water or gas bill for the property in the owner's name. Supporting only.</summary>
    UtilityBill = 3,

    /// <summary>Anything else that supports the request. Supporting only.</summary>
    Other = 4,

    /// <summary>A valid power of attorney or marketing authorization from the owner allowing this property to be advertised.</summary>
    Authorization = 5,

    /// <summary>The representative's brokerage license.</summary>
    BrokerageLicense = 6
}

/// <summary>The representative's own details, required when the request is made as an authorized representative.</summary>
public sealed record AuthorityRepresentative(string BrokerageLicenseNumber, string AgencyName, string PropertyOwnerName);

/// <summary>
/// A request to advertise one property for sale, for rent, or both: the listing content (price and terms) for each
/// purpose, who is asking and in what capacity (the owner, or an authorized representative), and the documents that
/// prove it. It is filled in step by step, submitted, and approved by two independent people.
/// </summary>
/// <remarks>
/// <para>
/// <b>Two approvals.</b> A legal reviewer checks the details, property data and documents; then a platform
/// administrator who is <b>not</b> that legal reviewer approves, which issues the grants and publishes the listings.
/// Neither may be the requester. Either can ask for completion (the request becomes a draft again with the reason; both
/// reviews are done again after the next submission) or reject it for a reason, which is final.
/// </para>
/// <para>
/// <b>Documents.</b> Each document is a chain of stored versions (<see cref="AuthorityRequestDocument"/> rows sharing a
/// <see cref="AuthorityRequestDocument.DocumentId"/>). Replacing adds a version and keeps the earlier ones; removing
/// keeps the record. Every version is malware-scanned, and nothing is submitted or approved unless every current
/// document is <see cref="DocumentScanStatus.Clean"/>. A document that was part of an approval cannot be removed.
/// </para>
/// <para>
/// <b>History.</b> Every submission and decision is appended to <see cref="Decisions"/> with who, when, why, the request
/// version and the exact document versions it covered.
/// </para>
/// <para>
/// Everything the requester controls changes only while the request is a <see cref="AuthorityRequestStatus.Draft"/>, so
/// what reviewers decide on is exactly what is stored. Every change moves <see cref="Version"/> on, the concurrency token.
/// </para>
/// </remarks>
public sealed class AuthorityRequest
{
    public const int MaxDocuments = 10;
    public const int MaxVersionsPerDocument = 20;
    public const long MaxDocumentBytes = 10L * 1024 * 1024;
    public const int MaxFileNameLength = 200;
    public const int MaxStorageKeyLength = 300;
    public const int MaxTextLength = 1000;
    public const int MaxNumberLength = 64;
    public const int MaxNameLength = 200;
    public const int MaxListingContentLength = 64 * 1024;
    public const int DefaultValidDays = 365;
    public const int MaxValidDays = 3650;

    /// <summary>The codes <see cref="Missing"/> may return.</summary>
    public static class MissingItem
    {
        public const string Details = "Details";
        public const string SaleListing = "SaleListing";
        public const string RentListing = "RentListing";
        public const string OwnershipProof = "OwnershipProof";
        public const string Authorization = "Authorization";
        public const string BrokerageLicense = "BrokerageLicense";

        /// <summary>A current document is still waiting for its malware scan.</summary>
        public const string DocumentScanPending = "DocumentScanPending";

        /// <summary>A current document was found infected or could not be scanned: replace or remove it, or ask for a rescan.</summary>
        public const string DocumentNotClean = "DocumentNotClean";
    }

    private readonly List<AuthorityRequestDocument> _documents = [];
    private readonly List<AuthorityRequestDecision> _decisions = [];

    private AuthorityRequest()
    {
    }

    public AuthorityRequest(
        Guid id,
        Guid propertyId,
        Guid requesterUserId,
        Guid requesterProfileId,
        bool requestsSale,
        bool requestsRent,
        DateTimeOffset createdAt)
    {
        if (id == Guid.Empty || propertyId == Guid.Empty || requesterUserId == Guid.Empty || requesterProfileId == Guid.Empty)
        {
            throw new ArgumentException("The request, property, requester and requester profile ids are required.");
        }

        if (!requestsSale && !requestsRent)
        {
            throw new ArgumentException("Ask for Sale, Rent, or both.");
        }

        Id = id;
        PropertyId = propertyId;
        RequesterUserId = requesterUserId;
        RequesterProfileId = requesterProfileId;
        RequestsSale = requestsSale;
        RequestsRent = requestsRent;
        Status = AuthorityRequestStatus.Draft;
        CreatedAt = createdAt;
        UpdatedAt = createdAt;
        Kind = AuthorityRequestKind.Listing;
    }

    /// <summary>
    /// A request to take over the ownership of a property registered to someone else (or to no one), bought on the
    /// platform (<paramref name="saleCommitmentId"/>) or outside it (null).
    /// </summary>
    public static AuthorityRequest ForOwnershipTransfer(
        Guid id,
        Guid propertyId,
        Guid requesterUserId,
        Guid requesterProfileId,
        bool requestsSale,
        bool requestsRent,
        Guid? previousOwnerUserId,
        Guid? saleCommitmentId,
        DateTimeOffset createdAt)
    {
        if (previousOwnerUserId == requesterUserId || previousOwnerUserId == Guid.Empty || saleCommitmentId == Guid.Empty)
        {
            throw new ArgumentException("The previous owner must be someone else, and given ids must not be empty.");
        }

        return new AuthorityRequest(id, propertyId, requesterUserId, requesterProfileId, requestsSale, requestsRent, createdAt)
        {
            Kind = AuthorityRequestKind.OwnershipTransfer,
            PreviousOwnerUserId = previousOwnerUserId,
            SaleCommitmentId = saleCommitmentId
        };
    }

    /// <summary>The fixed text recorded on requests closed by an ownership transfer.</summary>
    public const string OwnershipTransferredReason = "Ownership of the property was transferred to another owner.";

    public AuthorityRequestKind Kind { get; private set; }

    /// <summary>For an ownership transfer: who the property was registered to when the request started (null: no one).</summary>
    public Guid? PreviousOwnerUserId { get; private set; }

    /// <summary>For an ownership transfer of a platform sale: the completed sale commitment in which the requester bought it.</summary>
    public Guid? SaleCommitmentId { get; private set; }

    public bool IsOwnershipTransfer => Kind == AuthorityRequestKind.OwnershipTransfer;

    public Guid Id { get; private set; }
    public Guid PropertyId { get; private set; }

    /// <summary>The user asking: the property's registrant, acting as its owner or as the owner's representative.</summary>
    public Guid RequesterUserId { get; private set; }

    /// <summary>The requester's approved owner profile (their verified identity).</summary>
    public Guid RequesterProfileId { get; private set; }

    public bool RequestsSale { get; private set; }
    public bool RequestsRent { get; private set; }

    /// <summary>Owner or IndividualRepresentative; null until the requester chooses, which they must before submitting.</summary>
    public ListingRepresentationKind? Capacity { get; private set; }

    /// <summary>The number of the title deed or ownership document. Set together with <see cref="Capacity"/>.</summary>
    public string? OwnershipDocumentNumber { get; private set; }

    /// <summary>Set exactly when <see cref="Capacity"/> is IndividualRepresentative.</summary>
    public AuthorityRepresentative? Representative { get; private set; }

    public string? SaleListingContent { get; private set; }
    public string? RentListingContent { get; private set; }

    public AuthorityRequestStatus Status { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }
    public DateTimeOffset? SubmittedAt { get; private set; }

    public Guid? LegalReviewedByUserId { get; private set; }
    public DateTimeOffset? LegalReviewedAt { get; private set; }
    public string? LegalReviewNote { get; private set; }

    /// <summary>
    /// The property's last-change time the legal reviewer saw. If the property changes after that, the final approval
    /// is refused, because the reviewed data no longer describes it.
    /// </summary>
    public DateTimeOffset? ReviewedPropertyUpdatedAt { get; private set; }

    public Guid? IssuedByUserId { get; private set; }
    public DateTimeOffset? IssuedAt { get; private set; }
    public DateTimeOffset? GrantValidUntil { get; private set; }
    public Guid? SaleGrantId { get; private set; }
    public Guid? RentGrantId { get; private set; }

    /// <summary>The last time a reviewer asked for completion, by whom, why and from which stage. Kept after resubmission.</summary>
    public Guid? CompletionRequestedByUserId { get; private set; }
    public DateTimeOffset? CompletionRequestedAt { get; private set; }
    public string? CompletionRequestReason { get; private set; }
    public AuthorityRequestStatus? CompletionRequestedAtStage { get; private set; }

    /// <summary>The final rejection: by whom, why and from which stage. Set exactly when the status is Rejected.</summary>
    public Guid? RejectedByUserId { get; private set; }
    public DateTimeOffset? RejectedAt { get; private set; }
    public string? RejectionReason { get; private set; }
    public AuthorityRequestStatus? RejectedAtStage { get; private set; }

    public DateTimeOffset? WithdrawnAt { get; private set; }

    /// <summary>Moves on with every change; the concurrency token.</summary>
    public int Version { get; private set; } = 1;

    /// <summary>Every stored version of every document, including superseded and removed ones.</summary>
    public IReadOnlyList<AuthorityRequestDocument> Documents => _documents;

    /// <summary>The version in force of each document that has not been removed: what reviewers decide on.</summary>
    public IReadOnlyList<AuthorityRequestDocument> CurrentDocuments =>
        _documents.Where(document => document.IsCurrent).OrderBy(document => document.UploadedAt).ThenBy(document => document.Id).ToArray();

    /// <summary>Submissions and decisions, oldest first.</summary>
    public IReadOnlyList<AuthorityRequestDecision> Decisions => _decisions;

    public IReadOnlyList<ListingPurpose> Purposes =>
        (RequestsSale, RequestsRent) switch
        {
            (true, true) => [ListingPurpose.Sale, ListingPurpose.Rent],
            (true, false) => [ListingPurpose.Sale],
            _ => [ListingPurpose.Rent]
        };

    /// <summary>True until a final decision or a withdrawal: only one such request may exist per property.</summary>
    public bool IsOpen => Status is AuthorityRequestStatus.Draft
        or AuthorityRequestStatus.PendingLegalReview
        or AuthorityRequestStatus.PendingIssuance;

    /// <summary>What the request still lacks before it can be submitted or approved (codes of <see cref="MissingItem"/>).</summary>
    public IReadOnlyList<string> Missing
    {
        get
        {
            var missing = new List<string>();

            if (Capacity is null)
            {
                missing.Add(MissingItem.Details);
            }

            if (RequestsSale && SaleListingContent is null)
            {
                missing.Add(MissingItem.SaleListing);
            }

            if (RequestsRent && RentListingContent is null)
            {
                missing.Add(MissingItem.RentListing);
            }

            if (!Has(AuthorityDocumentType.TitleDeed) && !Has(AuthorityDocumentType.SaleContract))
            {
                missing.Add(MissingItem.OwnershipProof);
            }

            if (Capacity == ListingRepresentationKind.IndividualRepresentative)
            {
                if (!Has(AuthorityDocumentType.Authorization))
                {
                    missing.Add(MissingItem.Authorization);
                }

                if (!Has(AuthorityDocumentType.BrokerageLicense))
                {
                    missing.Add(MissingItem.BrokerageLicense);
                }
            }

            // Nothing is taken for clean: every current document must have been scanned and found clean.
            if (_documents.Any(document => document.IsCurrent && document.ScanStatus == DocumentScanStatus.PendingScan))
            {
                missing.Add(MissingItem.DocumentScanPending);
            }

            if (_documents.Any(document => document.IsCurrent
                && document.ScanStatus is DocumentScanStatus.Rejected or DocumentScanStatus.ScanFailed))
            {
                missing.Add(MissingItem.DocumentNotClean);
            }

            return missing;
        }
    }

    public string? ListingContentFor(ListingPurpose purpose) =>
        purpose == ListingPurpose.Sale ? SaleListingContent : RentListingContent;

    /// <summary>The version in force of one document, or null when there is none or it was removed.</summary>
    public AuthorityRequestDocument? CurrentVersionOf(Guid documentId) =>
        _documents.SingleOrDefault(document => document.DocumentId == documentId && document.IsCurrent);

    /// <summary>Every version of one document, oldest first.</summary>
    public IReadOnlyList<AuthorityRequestDocument> VersionsOf(Guid documentId) =>
        _documents.Where(document => document.DocumentId == documentId).OrderBy(document => document.VersionNumber).ToArray();

    /// <summary>True when any version of this document was covered by a legal or final approval.</summary>
    public bool WasApproved(Guid documentId)
    {
        var versions = _documents.Where(document => document.DocumentId == documentId).Select(document => document.Id).ToHashSet();

        return _decisions.Any(decision =>
            decision.Kind is AuthorityDecisionKind.LegalApproved or AuthorityDecisionKind.AdminApproved
            && decision.DocumentVersionIds.Any(versions.Contains));
    }

    public void UpdateDetails(
        ListingRepresentationKind capacity, string ownershipDocumentNumber, AuthorityRepresentative? representative, DateTimeOffset at)
    {
        EnsureStatus(AuthorityRequestStatus.Draft, "Only a draft request can be changed.");

        if (capacity is not (ListingRepresentationKind.Owner or ListingRepresentationKind.IndividualRepresentative))
        {
            throw new ArgumentException("The capacity must be Owner or IndividualRepresentative.", nameof(capacity));
        }

        var number = OptionalText(ownershipDocumentNumber, MaxNumberLength, nameof(ownershipDocumentNumber))
            ?? throw new ArgumentException("The ownership document number is required.", nameof(ownershipDocumentNumber));

        AuthorityRepresentative? details = null;

        if (capacity == ListingRepresentationKind.IndividualRepresentative)
        {
            if (representative is null)
            {
                throw new ArgumentException(
                    "A representative must give the brokerage license number, the agency name and the owner's name.",
                    nameof(representative));
            }

            details = new AuthorityRepresentative(
                OptionalText(representative.BrokerageLicenseNumber, MaxNumberLength, "brokerageLicenseNumber")
                    ?? throw new ArgumentException("The brokerage license number is required.", "brokerageLicenseNumber"),
                OptionalText(representative.AgencyName, MaxNameLength, "agencyName")
                    ?? throw new ArgumentException("The agency name is required.", "agencyName"),
                OptionalText(representative.PropertyOwnerName, MaxNameLength, "propertyOwnerName")
                    ?? throw new ArgumentException("The owner's name is required.", "propertyOwnerName"));
        }

        Capacity = capacity;
        OwnershipDocumentNumber = number;
        Representative = details;
        Touch(at);
    }

    public void SetListingContent(ListingPurpose purpose, string serializedContent, DateTimeOffset at)
    {
        EnsureStatus(AuthorityRequestStatus.Draft, "Only a draft request can be changed.");

        if (!Purposes.Contains(purpose))
        {
            throw new InvalidOperationException($"This request does not ask for {purpose}.");
        }

        if (string.IsNullOrWhiteSpace(serializedContent) || serializedContent.Length > MaxListingContentLength)
        {
            throw new ArgumentException("The listing content is required and must be reasonably small.", nameof(serializedContent));
        }

        if (purpose == ListingPurpose.Sale)
        {
            SaleListingContent = serializedContent;
        }
        else
        {
            RentListingContent = serializedContent;
        }

        Touch(at);
    }

    /// <summary>Adds a new document as its first version (quarantined, pending its scan).</summary>
    public void AddDocument(AuthorityRequestDocument version, DateTimeOffset at)
    {
        ArgumentNullException.ThrowIfNull(version);
        EnsureStatus(AuthorityRequestStatus.Draft, "Documents can only be added to a draft request.");
        EnsureOwnVersion(version);

        if (version.VersionNumber != 1 || _documents.Any(document => document.DocumentId == version.DocumentId))
        {
            throw new ArgumentException("A new document starts at version 1 with a new document id.", nameof(version));
        }

        if (_documents.Count(document => document.IsCurrent) >= MaxDocuments)
        {
            throw new InvalidOperationException($"A request may carry at most {MaxDocuments} documents.");
        }

        _documents.Add(version);
        Touch(at);
    }

    /// <summary>Replaces the version in force of a document with a new one. The earlier version is kept, superseded.</summary>
    public AuthorityRequestDocument ReplaceDocument(AuthorityRequestDocument version, DateTimeOffset at)
    {
        ArgumentNullException.ThrowIfNull(version);
        EnsureStatus(AuthorityRequestStatus.Draft, "Documents can only be replaced in a draft request.");
        EnsureOwnVersion(version);

        var current = CurrentVersionOf(version.DocumentId)
            ?? throw new InvalidOperationException("The document has no version in force.");
        var versions = VersionsOf(version.DocumentId);

        if (version.VersionNumber != versions[^1].VersionNumber + 1 || version.Type != current.Type)
        {
            throw new ArgumentException("A replacement is the next version of the same document type.", nameof(version));
        }

        if (versions.Count >= MaxVersionsPerDocument)
        {
            throw new InvalidOperationException($"A document may have at most {MaxVersionsPerDocument} versions.");
        }

        current.Supersede(at);
        _documents.Add(version);
        Touch(at);

        return current;
    }

    /// <summary>
    /// Removes a document from a draft; its record and versions are kept. Refused when any of its versions was part
    /// of an approval. Null when there is no such document in force.
    /// </summary>
    public AuthorityRequestDocument? RemoveDocument(Guid documentId, DateTimeOffset at)
    {
        EnsureStatus(AuthorityRequestStatus.Draft, "Documents can only be removed from a draft request.");

        var current = CurrentVersionOf(documentId);

        if (current is null)
        {
            return null;
        }

        if (WasApproved(documentId))
        {
            throw new InvalidOperationException("A document that was part of an approval cannot be removed; replace it instead.");
        }

        current.MarkRemoved(at);
        Touch(at);

        return current;
    }

    public void Submit(DateTimeOffset at)
    {
        EnsureStatus(AuthorityRequestStatus.Draft, "Only a draft request can be submitted.");
        EnsureComplete();

        Status = AuthorityRequestStatus.PendingLegalReview;
        SubmittedAt = at;
        Touch(at);
        Record(AuthorityDecisionKind.Submitted, RequesterUserId, null, attestedNoConflict: false, at);
    }

    public void Withdraw(DateTimeOffset at)
    {
        if (!IsOpen)
        {
            throw new InvalidOperationException("Only an open request can be withdrawn.");
        }

        Status = AuthorityRequestStatus.Withdrawn;
        WithdrawnAt = at;
        Touch(at);
        Record(AuthorityDecisionKind.Withdrawn, RequesterUserId, null, attestedNoConflict: false, at);
    }

    /// <summary>The legal reviewer accepts the details, property data and documents.</summary>
    public void ApproveLegalReview(
        Guid legalReviewerUserId, string? note, DateTimeOffset propertyUpdatedAt, bool attestedNoConflict, DateTimeOffset at)
    {
        EnsureStatus(AuthorityRequestStatus.PendingLegalReview, "The request is not awaiting legal review.");
        EnsureStaffDecision(legalReviewerUserId, attestedNoConflict);
        EnsureComplete();

        if (SubmittedAt is { } submitted && at < submitted)
        {
            throw new InvalidOperationException("A review cannot precede the submission.");
        }

        LegalReviewedByUserId = legalReviewerUserId;
        LegalReviewedAt = at;
        LegalReviewNote = OptionalText(note, MaxTextLength, nameof(note));
        ReviewedPropertyUpdatedAt = propertyUpdatedAt;
        Status = AuthorityRequestStatus.PendingIssuance;
        Touch(at);
        Record(AuthorityDecisionKind.LegalApproved, legalReviewerUserId, LegalReviewNote, attestedNoConflict, at);
    }

    /// <summary>
    /// Sends the request back to the requester as a draft, with what to complete. Both reviews are done again after
    /// the next submission.
    /// </summary>
    public void RequestCompletion(Guid reviewerUserId, string reason, bool attestedNoConflict, DateTimeOffset at)
    {
        var stage = EnsureAwaitingDecision(reviewerUserId, attestedNoConflict);
        var text = OptionalText(reason, MaxTextLength, nameof(reason))
            ?? throw new ArgumentException("A reason is required.", nameof(reason));

        CompletionRequestReason = text;
        CompletionRequestedByUserId = reviewerUserId;
        CompletionRequestedAt = at;
        CompletionRequestedAtStage = stage;
        Status = AuthorityRequestStatus.Draft;
        SubmittedAt = null;
        LegalReviewedByUserId = null;
        LegalReviewedAt = null;
        LegalReviewNote = null;
        ReviewedPropertyUpdatedAt = null;
        Touch(at);
        Record(
            stage == AuthorityRequestStatus.PendingLegalReview
                ? AuthorityDecisionKind.LegalCompletionRequested
                : AuthorityDecisionKind.AdminCompletionRequested,
            reviewerUserId, text, attestedNoConflict, at);
    }

    /// <summary>Refuses the request for a reason. Final.</summary>
    public void Reject(Guid reviewerUserId, string reason, bool attestedNoConflict, DateTimeOffset at)
    {
        var stage = EnsureAwaitingDecision(reviewerUserId, attestedNoConflict);
        var text = OptionalText(reason, MaxTextLength, nameof(reason))
            ?? throw new ArgumentException("A reason is required.", nameof(reason));

        RejectionReason = text;
        RejectedByUserId = reviewerUserId;
        RejectedAt = at;
        RejectedAtStage = stage;
        Status = AuthorityRequestStatus.Rejected;
        Touch(at);
        Record(
            stage == AuthorityRequestStatus.PendingLegalReview ? AuthorityDecisionKind.LegalRejected : AuthorityDecisionKind.AdminRejected,
            reviewerUserId, text, attestedNoConflict, at);
    }

    /// <summary>
    /// Closes an open request because another request transferred the property's ownership: a draft is withdrawn, a
    /// request awaiting a decision is rejected with <see cref="OwnershipTransferredReason"/>. Recorded by the issuer of
    /// the transfer, who must not be this requester.
    /// </summary>
    public void CloseForOwnershipTransfer(Guid issuerUserId, DateTimeOffset at)
    {
        if (!IsOpen)
        {
            throw new InvalidOperationException("Only an open request can be closed.");
        }

        if (issuerUserId == Guid.Empty || issuerUserId == RequesterUserId)
        {
            throw new InvalidOperationException("The transfer's issuer closes the request, never its requester.");
        }

        if (Status == AuthorityRequestStatus.Draft)
        {
            Status = AuthorityRequestStatus.Withdrawn;
            WithdrawnAt = at;
        }
        else
        {
            RejectionReason = OwnershipTransferredReason;
            RejectedByUserId = issuerUserId;
            RejectedAt = at;
            RejectedAtStage = Status;
            Status = AuthorityRequestStatus.Rejected;
        }

        Touch(at);
        Record(AuthorityDecisionKind.ClosedOwnershipTransferred, issuerUserId, OwnershipTransferredReason, attestedNoConflict: false, at);
    }

    /// <summary>Records the final approval. The grants are created and validated by <see cref="PropertyAuthorityGrant.Issue"/>.</summary>
    public void MarkIssued(
        Guid issuerUserId, DateTimeOffset validUntil, Guid? saleGrantId, Guid? rentGrantId, bool attestedNoConflict, DateTimeOffset at)
    {
        EnsureStatus(AuthorityRequestStatus.PendingIssuance, "The request is not awaiting the final approval.");
        EnsureStaffDecision(issuerUserId, attestedNoConflict);
        EnsureComplete();

        if (issuerUserId == LegalReviewedByUserId)
        {
            throw new InvalidOperationException("The approver must be a different person from the legal reviewer.");
        }

        if (RequestsSale != saleGrantId.HasValue || RequestsRent != rentGrantId.HasValue
            || saleGrantId == Guid.Empty || rentGrantId == Guid.Empty)
        {
            throw new ArgumentException("Exactly one grant is required for each requested purpose.");
        }

        if (validUntil <= at || LegalReviewedAt is not { } reviewed || at < reviewed)
        {
            throw new InvalidOperationException("The grant validity and the review chronology must hold.");
        }

        IssuedByUserId = issuerUserId;
        IssuedAt = at;
        GrantValidUntil = validUntil;
        SaleGrantId = saleGrantId;
        RentGrantId = rentGrantId;
        Status = AuthorityRequestStatus.Approved;
        Touch(at);
        Record(AuthorityDecisionKind.AdminApproved, issuerUserId, null, attestedNoConflict, at);
    }

    private bool Has(AuthorityDocumentType type) => _documents.Any(document => document.IsCurrent && document.Type == type);

    private void EnsureOwnVersion(AuthorityRequestDocument version)
    {
        if (version.RequestId != Id)
        {
            throw new ArgumentException("The document belongs to another request.", nameof(version));
        }

        if (version.ScanStatus != DocumentScanStatus.PendingScan)
        {
            throw new ArgumentException("A new document version starts quarantined, pending its scan.", nameof(version));
        }
    }

    private AuthorityRequestStatus EnsureAwaitingDecision(Guid reviewerUserId, bool attestedNoConflict)
    {
        if (Status is not (AuthorityRequestStatus.PendingLegalReview or AuthorityRequestStatus.PendingIssuance))
        {
            throw new InvalidOperationException("The request is not awaiting a decision.");
        }

        EnsureStaffDecision(reviewerUserId, attestedNoConflict);

        if (Status == AuthorityRequestStatus.PendingIssuance && reviewerUserId == LegalReviewedByUserId)
        {
            throw new InvalidOperationException("The legal reviewer cannot also take the final decision.");
        }

        return Status;
    }

    private void EnsureStaffDecision(Guid userId, bool attestedNoConflict)
    {
        if (userId == Guid.Empty)
        {
            throw new ArgumentException("The decision maker is required.", nameof(userId));
        }

        if (userId == RequesterUserId)
        {
            throw new InvalidOperationException("The requester cannot decide on their own request.");
        }

        if (userId == PreviousOwnerUserId)
        {
            throw new InvalidOperationException("The previous owner cannot decide on a transfer of their own property.");
        }

        if (!attestedNoConflict)
        {
            throw new InvalidOperationException("The reviewer must confirm they have no conflict of interest.");
        }
    }

    private void EnsureComplete()
    {
        if (Missing.Count > 0)
        {
            throw new InvalidOperationException($"Still missing: {string.Join(", ", Missing)}.");
        }
    }

    private void Record(AuthorityDecisionKind kind, Guid actorUserId, string? reason, bool attestedNoConflict, DateTimeOffset at)
    {
        _decisions.Add(new AuthorityRequestDecision(
            Guid.NewGuid(), Id, kind, actorUserId, at, reason, Version, attestedNoConflict,
            CurrentDocuments.Select(document => document.Id).ToArray()));
    }

    private void EnsureStatus(AuthorityRequestStatus expected, string message)
    {
        if (Status != expected)
        {
            throw new InvalidOperationException(message);
        }
    }

    private void Touch(DateTimeOffset at)
    {
        UpdatedAt = at;
        Version = checked(Version + 1);
    }

    internal static string? OptionalText(string? value, int maxLength, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim();

        return trimmed.Length <= maxLength
            ? trimmed
            : throw new ArgumentException($"At most {maxLength} characters.", parameterName);
    }
}

/// <summary>
/// One stored version of a document attached to an authority request. Versions of the same document share
/// <see cref="DocumentId"/>. The bytes live in private storage under a generated key; they are quarantined until the
/// malware scan marks them <see cref="DocumentScanStatus.Clean"/>.
/// </summary>
public sealed class AuthorityRequestDocument
{
    public const string Pdf = "application/pdf";
    public const int MaxScanDetailLength = 200;

    private AuthorityRequestDocument()
    {
        StorageKey = null!;
        ContentType = null!;
        Sha256 = null!;
    }

    public AuthorityRequestDocument(
        Guid id,
        Guid requestId,
        Guid documentId,
        int versionNumber,
        AuthorityDocumentType type,
        string storageKey,
        string contentType,
        string? fileName,
        long sizeBytes,
        string sha256,
        Guid uploadedByUserId,
        DateTimeOffset uploadedAt)
    {
        if (id == Guid.Empty || requestId == Guid.Empty || documentId == Guid.Empty || uploadedByUserId == Guid.Empty)
        {
            throw new ArgumentException("The version, request, document and uploader ids are required.");
        }

        if (versionNumber < 1)
        {
            throw new ArgumentException("Versions are numbered from 1.", nameof(versionNumber));
        }

        if (!Enum.IsDefined(type))
        {
            throw new ArgumentException("Unknown document type.", nameof(type));
        }

        if (string.IsNullOrWhiteSpace(storageKey) || storageKey.Length > AuthorityRequest.MaxStorageKeyLength)
        {
            throw new ArgumentException("A storage key is required.", nameof(storageKey));
        }

        if (contentType is not ("image/jpeg" or "image/png" or "image/webp" or Pdf))
        {
            throw new ArgumentException("Only JPEG, PNG, WebP and PDF documents are stored.", nameof(contentType));
        }

        if (sizeBytes is <= 0 or > AuthorityRequest.MaxDocumentBytes)
        {
            throw new ArgumentException("The document size is out of range.", nameof(sizeBytes));
        }

        if (sha256 is null || sha256.Length != 64 || sha256.Any(character => !Uri.IsHexDigit(character)))
        {
            throw new ArgumentException("The digest must be a 64-character SHA-256 hexadecimal value.", nameof(sha256));
        }

        Id = id;
        RequestId = requestId;
        DocumentId = documentId;
        VersionNumber = versionNumber;
        Type = type;
        StorageKey = storageKey;
        ContentType = contentType;
        FileName = string.IsNullOrWhiteSpace(fileName)
            ? null
            : Path.GetFileName(fileName.Trim().Replace('\\', '/')) is { Length: > 0 } name
                ? name[..Math.Min(name.Length, AuthorityRequest.MaxFileNameLength)]
                : null;
        SizeBytes = sizeBytes;
        Sha256 = sha256.ToUpperInvariant();
        UploadedByUserId = uploadedByUserId;
        UploadedAt = uploadedAt;
        ScanStatus = DocumentScanStatus.PendingScan;
        NextScanAt = uploadedAt;
    }

    /// <summary>This version's id.</summary>
    public Guid Id { get; private set; }
    public Guid RequestId { get; private set; }

    /// <summary>The document this is a version of; the same for every version.</summary>
    public Guid DocumentId { get; private set; }
    public int VersionNumber { get; private set; }
    public AuthorityDocumentType Type { get; private set; }

    /// <summary>The generated private storage key. Changes once, when a clean file leaves quarantine.</summary>
    public string StorageKey { get; private set; }
    public string ContentType { get; private set; }
    public string? FileName { get; private set; }
    public long SizeBytes { get; private set; }

    /// <summary>SHA-256 of the stored bytes, upper-case hex. Checked before every scan.</summary>
    public string Sha256 { get; private set; }
    public Guid UploadedByUserId { get; private set; }
    public DateTimeOffset UploadedAt { get; private set; }

    public DateTimeOffset? SupersededAt { get; private set; }
    public DateTimeOffset? RemovedAt { get; private set; }

    public DocumentScanStatus ScanStatus { get; private set; }
    public int ScanAttempts { get; private set; }

    /// <summary>When the scanner should next look at it; null once there is a final verdict.</summary>
    public DateTimeOffset? NextScanAt { get; private set; }
    public DateTimeOffset? ScannedAt { get; private set; }

    /// <summary>Which scanner gave the verdict (for example "ClamAV 1.4.1/27450").</summary>
    public string? ScanEngine { get; private set; }

    /// <summary>The signature found, or the last scan error. Staff-only.</summary>
    public string? ScanDetail { get; private set; }

    /// <summary>Moves on with every change of this row; the concurrency token.</summary>
    public int RowVersion { get; private set; } = 1;

    public bool IsCurrent => SupersededAt is null && RemovedAt is null;

    /// <summary>The bytes are verified and may be served.</summary>
    public bool IsAvailable => ScanStatus == DocumentScanStatus.Clean;

    public void MarkClean(string engine, string cleanStorageKey, DateTimeOffset at)
    {
        EnsurePending();

        if (string.IsNullOrWhiteSpace(cleanStorageKey) || cleanStorageKey.Length > AuthorityRequest.MaxStorageKeyLength)
        {
            throw new ArgumentException("A storage key is required.", nameof(cleanStorageKey));
        }

        ScanStatus = DocumentScanStatus.Clean;
        StorageKey = cleanStorageKey;
        Finish(engine, null, at);
    }

    public void MarkInfected(string engine, string signature, DateTimeOffset at)
    {
        EnsurePending();
        ScanStatus = DocumentScanStatus.Rejected;
        Finish(engine, signature, at);
    }

    /// <summary>
    /// Records a scan that gave no verdict. Retries at <paramref name="retryAt"/> until <paramref name="maxAttempts"/>
    /// attempts were made, then the document is ScanFailed.
    /// </summary>
    public void RecordScanError(string error, int maxAttempts, DateTimeOffset retryAt, DateTimeOffset at)
    {
        EnsurePending();

        if (maxAttempts < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maxAttempts));
        }

        ScanAttempts++;
        ScanDetail = Clip(error);

        if (ScanAttempts >= maxAttempts)
        {
            ScanStatus = DocumentScanStatus.ScanFailed;
            NextScanAt = null;
            ScannedAt = at;
        }
        else
        {
            NextScanAt = retryAt;
        }

        RowVersion++;
    }

    /// <summary>Puts a document that could not be scanned back in the queue, with a fresh set of attempts.</summary>
    public void RequestRescan(DateTimeOffset at)
    {
        if (ScanStatus != DocumentScanStatus.ScanFailed)
        {
            throw new InvalidOperationException("Only a document whose scan failed can be scanned again.");
        }

        ScanStatus = DocumentScanStatus.PendingScan;
        ScanAttempts = 0;
        NextScanAt = at;
        ScannedAt = null;
        RowVersion++;
    }

    internal void Supersede(DateTimeOffset at)
    {
        SupersededAt = at;
        RowVersion++;
    }

    internal void MarkRemoved(DateTimeOffset at)
    {
        RemovedAt = at;
        RowVersion++;
    }

    private void EnsurePending()
    {
        if (ScanStatus != DocumentScanStatus.PendingScan)
        {
            throw new InvalidOperationException("The document is not waiting for a scan.");
        }
    }

    private void Finish(string engine, string? detail, DateTimeOffset at)
    {
        ScanAttempts++;
        ScanEngine = Clip(engine);
        ScanDetail = Clip(detail);
        ScannedAt = at;
        NextScanAt = null;
        RowVersion++;
    }

    private static string? Clip(string? text) =>
        string.IsNullOrWhiteSpace(text) ? null : text.Trim()[..Math.Min(text.Trim().Length, MaxScanDetailLength)];
}

/// <summary>
/// One submission or decision on a request: who, when, why, the request version it produced and the exact document
/// versions in force at that moment. Appended, never changed or deleted.
/// </summary>
public sealed class AuthorityRequestDecision
{
    private AuthorityRequestDecision()
    {
        DocumentVersionIds = [];
    }

    public AuthorityRequestDecision(
        Guid id,
        Guid requestId,
        AuthorityDecisionKind kind,
        Guid actorUserId,
        DateTimeOffset at,
        string? reason,
        int requestVersion,
        bool attestedNoConflict,
        Guid[] documentVersionIds)
    {
        if (id == Guid.Empty || requestId == Guid.Empty || actorUserId == Guid.Empty)
        {
            throw new ArgumentException("The decision, request and actor ids are required.");
        }

        ArgumentNullException.ThrowIfNull(documentVersionIds);

        Id = id;
        RequestId = requestId;
        Kind = kind;
        ActorUserId = actorUserId;
        At = at;
        Reason = reason;
        RequestVersion = requestVersion;
        AttestedNoConflict = attestedNoConflict;
        DocumentVersionIds = documentVersionIds;
    }

    public Guid Id { get; private set; }
    public Guid RequestId { get; private set; }
    public AuthorityDecisionKind Kind { get; private set; }
    public Guid ActorUserId { get; private set; }
    public DateTimeOffset At { get; private set; }
    public string? Reason { get; private set; }
    public int RequestVersion { get; private set; }

    /// <summary>The reviewer confirmed they have no conflict of interest with the requester or the named owner.</summary>
    public bool AttestedNoConflict { get; private set; }

    /// <summary>The document versions in force when this was decided.</summary>
    public Guid[] DocumentVersionIds { get; private set; }
}
