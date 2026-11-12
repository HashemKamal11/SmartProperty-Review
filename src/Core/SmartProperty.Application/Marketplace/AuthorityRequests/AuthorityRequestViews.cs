using SmartProperty.Domain.Marketplace;

namespace SmartProperty.Application.Marketplace.AuthorityRequests;

/// <summary>
/// One document as it stands: its version in force (or the last one when removed). <c>scanStatus</c> is PendingScan,
/// Clean, Rejected or ScanFailed; only a Clean document can be downloaded. <c>lockedByApproval</c> is true when a version
/// of it was part of an approval, so it can be replaced but not removed. <c>scanEngine</c> and <c>scanDetail</c> (the
/// signature found or the last scan error) are filled in for staff only.
/// </summary>
public sealed record AuthorityDocumentView(
    Guid DocumentId,
    Guid RequestId,
    Guid VersionId,
    int VersionNumber,
    string Type,
    string ContentType,
    string? FileName,
    long SizeBytes,
    string Sha256,
    DateTimeOffset UploadedAt,
    string ScanStatus,
    DateTimeOffset? ScannedAt,
    int ScanAttempts,
    bool IsCurrent,
    DateTimeOffset? SupersededAt,
    DateTimeOffset? RemovedAt,
    bool LockedByApproval,
    string? ScanEngine,
    string? ScanDetail)
{
    internal static AuthorityDocumentView From(AuthorityRequest request, AuthorityRequestDocument version, bool forStaff) =>
        new(
            version.DocumentId,
            version.RequestId,
            version.Id,
            version.VersionNumber,
            version.Type.ToString(),
            version.ContentType,
            version.FileName,
            version.SizeBytes,
            version.Sha256,
            version.UploadedAt,
            version.ScanStatus.ToString(),
            version.ScannedAt,
            version.ScanAttempts,
            version.IsCurrent,
            version.SupersededAt,
            version.RemovedAt,
            request.WasApproved(version.DocumentId),
            forStaff ? version.ScanEngine : null,
            forStaff ? version.ScanDetail : null);
}

/// <summary>A document with every one of its versions, oldest first.</summary>
public sealed record AuthorityDocumentDetailView(AuthorityDocumentView Document, IReadOnlyList<AuthorityDocumentView> Versions)
{
    internal static AuthorityDocumentDetailView From(AuthorityRequest request, Guid documentId, bool forStaff)
    {
        var versions = request.VersionsOf(documentId);
        var shown = request.CurrentVersionOf(documentId) ?? versions[^1];

        return new AuthorityDocumentDetailView(
            AuthorityDocumentView.From(request, shown, forStaff),
            versions.Select(version => AuthorityDocumentView.From(request, version, forStaff)).ToArray());
    }
}

/// <summary>
/// One recorded step: Submitted, LegalApproved, LegalCompletionRequested, LegalRejected, AdminApproved,
/// AdminCompletionRequested, AdminRejected or Withdrawn, with the document versions in force at that moment. Who acted
/// is shown to staff only.
/// </summary>
public sealed record AuthorityDecisionView(
    string Kind,
    DateTimeOffset At,
    string? Reason,
    int RequestVersion,
    IReadOnlyList<Guid> DocumentVersionIds,
    Guid? ActorUserId,
    bool? AttestedNoConflict);

/// <summary>
/// One authority request, with the listing content it will publish. Staff user ids are filled in only for staff
/// readers. <c>missing</c> (drafts only) lists what is still needed before submitting: Details, SaleListing,
/// RentListing, OwnershipProof, Authorization, BrokerageLicense, DocumentScanPending, DocumentNotClean.
/// <c>documents</c> are the documents in force. A request a reviewer asked to complete is a Draft with
/// <c>completionRequestReason</c>; a rejected one is final with <c>rejectionReason</c>.
/// </summary>
public sealed record AuthorityRequestView(
    Guid RequestId,
    Guid PropertyId,
    Guid RequesterUserId,
    string Status,
    IReadOnlyList<string> Purposes,
    string? Capacity,
    string? OwnershipDocumentNumber,
    AuthorityRepresentativeView? Representative,
    IReadOnlyList<string> Missing,
    ListingDraftContentInputView? SaleListing,
    ListingDraftContentInputView? RentListing,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    int Version,
    DateTimeOffset? SubmittedAt,
    DateTimeOffset? LegalReviewedAt,
    string? LegalReviewNote,
    DateTimeOffset? IssuedAt,
    DateTimeOffset? GrantValidUntil,
    Guid? SaleGrantId,
    Guid? RentGrantId,
    DateTimeOffset? CompletionRequestedAt,
    string? CompletionRequestReason,
    string? CompletionRequestedAtStage,
    DateTimeOffset? RejectedAt,
    string? RejectionReason,
    string? RejectedAtStage,
    DateTimeOffset? WithdrawnAt,
    IReadOnlyList<AuthorityDocumentView> Documents,
    IReadOnlyList<AuthorityDecisionView> Decisions,
    Guid? LegalReviewedByUserId,
    Guid? IssuedByUserId,
    Guid? CompletionRequestedByUserId,
    Guid? RejectedByUserId)
{
    internal static AuthorityRequestView From(AuthorityRequest request, bool forStaff)
    {
        ArgumentNullException.ThrowIfNull(request);

        return new AuthorityRequestView(
            request.Id,
            request.PropertyId,
            request.RequesterUserId,
            request.Status.ToString(),
            request.Purposes.Select(purpose => purpose.ToString()).ToArray(),
            request.Capacity?.ToString(),
            request.OwnershipDocumentNumber,
            request.Representative is { } representative
                ? new AuthorityRepresentativeView(representative.BrokerageLicenseNumber, representative.AgencyName, representative.PropertyOwnerName)
                : null,
            request.Status == AuthorityRequestStatus.Draft ? request.Missing : [],
            ListingDraftContentInputView.From(AuthorityListingContent.Deserialize(request.SaleListingContent)),
            ListingDraftContentInputView.From(AuthorityListingContent.Deserialize(request.RentListingContent)),
            request.CreatedAt,
            request.UpdatedAt,
            request.Version,
            request.SubmittedAt,
            request.LegalReviewedAt,
            request.LegalReviewNote,
            request.IssuedAt,
            request.GrantValidUntil,
            request.SaleGrantId,
            request.RentGrantId,
            request.CompletionRequestedAt,
            request.CompletionRequestReason,
            request.CompletionRequestedAtStage?.ToString(),
            request.RejectedAt,
            request.RejectionReason,
            request.RejectedAtStage?.ToString(),
            request.WithdrawnAt,
            request.CurrentDocuments.Select(document => AuthorityDocumentView.From(request, document, forStaff)).ToArray(),
            request.Decisions
                .OrderBy(decision => decision.At).ThenBy(decision => decision.RequestVersion)
                .Select(decision => new AuthorityDecisionView(
                    decision.Kind.ToString(),
                    decision.At,
                    decision.Reason,
                    decision.RequestVersion,
                    decision.DocumentVersionIds,
                    forStaff ? decision.ActorUserId : null,
                    forStaff ? decision.AttestedNoConflict : null))
                .ToArray(),
            forStaff ? request.LegalReviewedByUserId : null,
            forStaff ? request.IssuedByUserId : null,
            forStaff ? request.CompletionRequestedByUserId : null,
            forStaff ? request.RejectedByUserId : null);
    }
}

/// <summary>The stored listing content, shaped like the listing-draft request body (enum values as names).</summary>
public sealed record ListingDraftContentInputView(
    string Title,
    string Description,
    decimal Amount,
    string? Currency,
    string? RentPeriod,
    IReadOnlyList<Guid> EvidenceIds,
    IReadOnlyList<PricingOptionView>? PricingOptions,
    RentalTermsView? RentalTerms)
{
    internal static ListingDraftContentInputView? From(ListingSubmissions.Write.ListingDraftContentInput? input)
    {
        return input is null
            ? null
            : new ListingDraftContentInputView(
                input.Title,
                input.Description,
                input.Amount,
                string.IsNullOrEmpty(input.Currency) ? null : input.Currency,
                input.RentPeriod?.ToString(),
                input.EvidenceIds,
                input.PricingOptions?.Select(option => new PricingOptionView(option.Period.ToString(), option.Amount, option.Currency)).ToArray(),
                input.RentalTerms is { } terms
                    ? new RentalTermsView(
                        terms.CheckInTime,
                        terms.CheckOutTime,
                        terms.Windows.Select(window => new RentalWindowView(window.StartDate, window.EndDate)).ToArray(),
                        terms.MinimumStayDays)
                    : null);
    }
}

public sealed record PricingOptionView(string Period, decimal Amount, string Currency);

public sealed record RentalWindowView(DateOnly StartDate, DateOnly EndDate);

public sealed record RentalTermsView(TimeOnly CheckInTime, TimeOnly CheckOutTime, IReadOnlyList<RentalWindowView> Windows, int? MinimumStayDays);

public sealed record AuthorityRequestPage(IReadOnlyList<AuthorityRequestView> Items, int Page, int PageSize, int TotalCount);

/// <summary>A grant as staff and its owner see it. <c>State</c> is Active, Expired or Revoked.</summary>
public sealed record AuthorityGrantSummary(
    Guid GrantId,
    long Version,
    Guid PropertyId,
    Guid GranteeUserId,
    string Purpose,
    string State,
    DateTimeOffset ValidFrom,
    DateTimeOffset ValidUntil,
    DateTimeOffset IssuedAt,
    Guid LegalReviewReferenceId,
    DateTimeOffset? RevokedAt,
    Guid? IssuedByUserId,
    Guid? LegalReviewedByUserId,
    Guid? RevokedByUserId)
{
    internal static AuthorityGrantSummary From(PropertyAuthorityGrant grant, DateTimeOffset now, bool forStaff)
    {
        ArgumentNullException.ThrowIfNull(grant);

        var validUntil = grant.Authority.ValidUntil ?? DateTimeOffset.MaxValue;
        var state = grant.RevokedAt is not null ? "Revoked" : now >= validUntil ? "Expired" : "Active";

        return new AuthorityGrantSummary(
            grant.Id,
            grant.Version,
            grant.PropertyId,
            grant.GranteeUserId,
            grant.Purpose.ToString(),
            state,
            grant.Authority.ValidFrom,
            validUntil,
            grant.IssuedAt,
            grant.LegalReviewReferenceId,
            grant.RevokedAt,
            forStaff ? grant.IssuedByUserId : null,
            forStaff ? grant.LegalReviewedByUserId : null,
            forStaff ? grant.RevokedByUserId : null);
    }
}

/// <summary>An opened document, for download.</summary>
public sealed record AuthorityDocumentFile(Stream Content, string ContentType, string? FileName);

public sealed record AuthorityRepresentativeView(string BrokerageLicenseNumber, string AgencyName, string PropertyOwnerName);

/// <summary>
/// One kind of document that can be attached. <c>Requirement</c> is <c>OneOfOwnershipProof</c> (a TitleDeed or a
/// SaleContract is required from everyone), <c>RequiredForRepresentative</c> (required when acting as
/// IndividualRepresentative) or <c>Optional</c>.
/// </summary>
public sealed record AuthorityDocumentTypeInfo(string Type, string Requirement, string DescriptionEn, string DescriptionAr);

/// <summary>What to attach, by capacity.</summary>
public static class AuthorityDocumentCatalog
{
    public const string OneOfOwnershipProof = "OneOfOwnershipProof";
    public const string RequiredForRepresentative = "RequiredForRepresentative";
    public const string Optional = "Optional";

    public static IReadOnlyList<AuthorityDocumentTypeInfo> All { get; } =
    [
        new(nameof(AuthorityDocumentType.TitleDeed), OneOfOwnershipProof,
            "Registered title deed or ownership certificate (Saudi deed, Egyptian registered contract, UAE title deed). A clear photo or scan of every page.",
            "صك الملكية أو شهادة الملكية المسجلة (صك سعودي، عقد مسجل بالشهر العقاري في مصر، سند ملكية في الإمارات). صورة أو مسح واضح لكل الصفحات."),
        new(nameof(AuthorityDocumentType.SaleContract), OneOfOwnershipProof,
            "The contract under which the owner bought the property, signed by both parties, when there is no registered deed yet.",
            "عقد شراء العقار موقّعًا من الطرفين، في حال عدم وجود صك أو عقد مسجل بعد."),
        new(nameof(AuthorityDocumentType.Authorization), RequiredForRepresentative,
            "A valid power of attorney or marketing authorization from the owner allowing you to advertise this property.",
            "تفويض ساري أو وكالة شرعية من المالك تسمح لك بعرض هذا العقار وتسويقه."),
        new(nameof(AuthorityDocumentType.BrokerageLicense), RequiredForRepresentative,
            "Your valid brokerage license.",
            "رخصة الوساطة العقارية السارية."),
        new(nameof(AuthorityDocumentType.UtilityBill), Optional,
            "A recent electricity, water or gas bill for this property in the owner's name. Supporting only.",
            "فاتورة كهرباء أو مياه أو غاز حديثة لهذا العقار باسم المالك. مستند داعم فقط."),
        new(nameof(AuthorityDocumentType.Other), Optional,
            "Anything else that supports the request. Supporting only.",
            "أي مستند آخر يدعم الطلب. مستند داعم فقط.")
    ];
}
