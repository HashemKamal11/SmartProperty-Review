using SmartProperty.Common.Results;
using SmartProperty.Domain.Marketplace;

namespace SmartProperty.Application.Marketplace.AuthorityRequests;

public static class AuthorityRequestErrors
{
    public static readonly Error NotFound = new(
        "authority_requests.not_found", "The authority request was not found.", ErrorType.NotFound);

    public static readonly Error DocumentNotFound = new(
        "authority_requests.document_not_found", "The document was not found.", ErrorType.NotFound);

    public static readonly Error GrantNotFound = new(
        "authority_grants.not_found", "The authority grant was not found.", ErrorType.NotFound);

    public static readonly Error PropertyNotFound = new(
        "properties.not_found", "The property was not found.", ErrorType.NotFound);

    public static readonly Error Forbidden = new(
        "authorization.forbidden", "You do not have permission to access this resource.", ErrorType.Forbidden);

    public static readonly Error NotPropertyOwner = new(
        "authority_requests.not_property_owner", "Only the user who registered the property can ask to advertise it.", ErrorType.Forbidden);

    public static readonly Error OwnerNotApproved = new(
        "authority_requests.owner_not_approved", "Your owner profile must be approved first.", ErrorType.Conflict);

    public static readonly Error PropertyNotActive = new(
        "authority_requests.property_not_active", "The property is archived.", ErrorType.Conflict);

    public static readonly Error AlreadyOpen = new(
        "authority_requests.already_open", "This property already has an open authority request.", ErrorType.Conflict);

    public static readonly Error AlreadyGranted = new(
        "authority_requests.already_granted", "You already hold authority for this purpose on this property.", ErrorType.Conflict);

    public static readonly Error ExpiredGrantNotRevoked = new(
        "authority_requests.expired_grant_not_revoked",
        "An expired grant for this purpose is still on record; an administrator must revoke it before a new one can be issued.",
        ErrorType.Conflict);

    public static readonly Error NotEditable = new(
        "authority_requests.not_editable", "Documents can only be changed while the request is a draft.", ErrorType.Conflict);

    public static readonly Error InvalidState = new(
        "authority_requests.invalid_state", "The request is not in a state that allows this.", ErrorType.Conflict);

    /// <summary>
    /// Submitting needs the details, the listing content for every requested purpose, a TitleDeed or SaleContract, and
    /// for a representative also an Authorization and a BrokerageLicense. The codes are those of <c>missing</c>.
    /// </summary>
    public static Error Incomplete(IReadOnlyList<string> missing) => new(
        "authority_requests.incomplete",
        $"Still missing before submitting: {string.Join(", ", missing)}.",
        ErrorType.Validation);

    public static readonly Error PurposeNotRequested = new(
        "authority_requests.purpose_not_requested", "This request does not ask for that purpose.", ErrorType.Validation);

    /// <summary>The final approval could not publish a listing; nothing was approved or issued.</summary>
    public static Error CannotPublish(string reason) => new(
        "authority_requests.cannot_publish", $"The listing could not be published: {reason}", ErrorType.Conflict);

    public static readonly Error SamePersonForBothDecisions = new(
        "authority_requests.issuer_is_legal_reviewer",
        "The grants must be issued by a different person from the legal reviewer.",
        ErrorType.Conflict);

    public static readonly Error NotIndependent = new(
        "authority_requests.reviewer_not_independent", "You are not independent of this owner.", ErrorType.Forbidden);

    public static readonly Error IndependenceUnverifiable = new(
        "authority_requests.independence_unverifiable",
        "Your independence of this owner could not be established.",
        ErrorType.Forbidden);

    public static readonly Error CannotIssue = new(
        "authority_requests.cannot_issue",
        "The grants can no longer be issued: the property, the owner's approval or the legal reviewer changed.",
        ErrorType.Conflict);

    public static readonly Error GrantNotRevocable = new(
        "authority_grants.not_revocable", "The grant is already revoked or the version is stale.", ErrorType.Conflict);

    public static readonly Error ConcurrentChange = new(
        "authority_requests.concurrent_change", "Someone else changed this at the same time. Reload and try again.", ErrorType.Conflict);

    public static readonly Error DocumentNotAvailable = new(
        "authority_requests.document_not_available",
        "The document cannot be downloaded: it has not passed its malware scan.",
        ErrorType.Conflict);

    public static readonly Error DocumentLockedByApproval = new(
        "authority_requests.document_locked_by_approval",
        "This document was part of an approval and cannot be removed. Upload a replacement instead.",
        ErrorType.Conflict);

    public static readonly Error DocumentNotRescannable = new(
        "authority_requests.document_not_rescannable",
        "Only a document whose malware scan failed can be scanned again.",
        ErrorType.Conflict);

    public static readonly Error TooManyVersions = new(
        "authority_requests.too_many_versions",
        $"A document may have at most {AuthorityRequest.MaxVersionsPerDocument} versions.",
        ErrorType.Conflict);

    public static readonly Error ConflictAttestationRequired = new(
        "authority_requests.conflict_attestation_required",
        "Confirm that you have no conflict of interest with the requester or the property owner (noConflictOfInterest: true).",
        ErrorType.Validation);

    public static readonly Error PropertyChangedSinceReview = new(
        "authority_requests.property_changed_since_review",
        "The property was changed after the legal review. Ask the requester to complete the request so it is reviewed again.",
        ErrorType.Conflict);

    public static readonly Error StorageUnavailable = new(
        "authority_requests.storage_unavailable",
        "The document could not be stored. Try again later.",
        ErrorType.ServiceUnavailable);

    public static Error Validation(string description) => new("validation.failed", description, ErrorType.Validation);
}
