#nullable enable
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SmartProperty.Api.Contracts;
using SmartProperty.Api.Infrastructure.Errors;
using SmartProperty.Application.Marketplace.AuthorityRequests;
using SmartProperty.Application.PropertyRegistry;
using SmartProperty.Common.Results;

namespace SmartProperty.Api.Controllers;

/// <summary>
/// Start a request: the property and the purposes (<c>["Sale"]</c>, <c>["Rent"]</c> or both). The capacity
/// (<c>Owner</c> or <c>IndividualRepresentative</c>) and the ownership document number may be given now or later with
/// <c>PUT</c>; a representative also gives the brokerage license number, the agency name and the owner's name.
/// </summary>
public sealed record CreateAuthorityRequestRequest(
    Guid? PropertyId,
    IReadOnlyList<string>? Purposes,
    string? Capacity,
    string? OwnershipDocumentNumber,
    string? BrokerageLicenseNumber = null,
    string? AgencyName = null,
    string? PropertyOwnerName = null);

/// <summary>Changes the capacity and details of a draft. Same fields as when starting it.</summary>
public sealed record UpdateAuthorityRequestRequest(
    string? Capacity,
    string? OwnershipDocumentNumber,
    string? BrokerageLicenseNumber = null,
    string? AgencyName = null,
    string? PropertyOwnerName = null);

/// <summary>
/// The legal reviewer's approval. <c>noConflictOfInterest</c> must be true: the reviewer confirms they have no personal,
/// family or business relationship with the requester or the property owner. It is recorded with the decision.
/// </summary>
public sealed record LegalApprovalRequest(string? Note, bool? NoConflictOfInterest);

/// <summary>How long the grants last, in days (1 to 3650; omit for 365), and the administrator's no-conflict confirmation.</summary>
public sealed record IssueGrantsRequest(int? ValidDays, bool? NoConflictOfInterest);

/// <summary>A reason (shown to the requester) and the reviewer's no-conflict confirmation.</summary>
public sealed record AuthorityDecisionRequest(string? Reason, bool? NoConflictOfInterest);

public sealed record RevokeGrantRequest(long? ExpectedVersion);

/// <summary>
/// The requester's side of the add-property wizard after the property exists: one request carries the listing
/// content, the capacity and details, and the documents, and is submitted once. A legal reviewer then checks it, and a
/// different platform administrator approves it, which issues the grants and publishes the listings.
/// </summary>
/// <remarks>
/// Flow: <c>POST</c> (Draft, with the purposes) → <c>PUT {id}/listings/{purpose}</c> (price and terms) →
/// <c>PUT {id}</c> (capacity and details) → <c>POST {id}/documents</c> (one file) or <c>POST {id}/documents/batch</c> (several; each is malware-scanned) →
/// <c>POST {id}/submit</c> once <c>missing</c> is empty. A request a reviewer asked to complete is a draft again, with
/// <c>completionRequestReason</c>; a rejected one is final. Only the user who registered the property can start a
/// request, and only with an approved owner profile.
/// </remarks>
[ApiController]
[Authorize]
[Route("api/marketplace/me/authority-requests")]
public sealed class AuthorityRequestsController(OwnerAuthorityRequests requests) : ControllerBase
{
    internal const long MaxUploadBytes = 11L * 1024 * 1024;

    /// <summary>The most one batch upload may carry: the document limit times the per-file limit, with room for the form.</summary>
    internal const long MaxBatchUploadBytes = 101L * 1024 * 1024;

    /// <summary>What the owner can attach, with what each document is, in English and Arabic. Public.</summary>
    [HttpGet("~/api/marketplace/authority-document-types")]
    [AllowAnonymous]
    [ProducesResponseType<IReadOnlyList<AuthorityDocumentTypeInfo>>(StatusCodes.Status200OK)]
    public IActionResult DocumentTypes() => Ok(AuthorityDocumentCatalog.All);

    [HttpPost]
    [ProducesResponseType<AuthorityRequestView>(StatusCodes.Status201Created)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> Create(CreateAuthorityRequestRequest request, CancellationToken cancellationToken)
    {
        var details = new AuthorityRequestDetailsInput(
            request.Capacity, request.OwnershipDocumentNumber, request.BrokerageLicenseNumber, request.AgencyName, request.PropertyOwnerName);
        var result = await requests.CreateAsync(request.PropertyId ?? Guid.Empty, request.Purposes, details, cancellationToken);

        return result.IsFailure ? Fail(result.Error!) : StatusCode(StatusCodes.Status201Created, result.Value);
    }

    /// <summary>
    /// Starts an ownership-transfer request: the caller bought a property registered to someone else (on the platform
    /// or outside it) and asks to become its owner here and advertise it. Same body and same next steps as a normal
    /// request (listing content, documents with a title deed or sale contract, submit); a lawyer and then an
    /// administrator review it. On approval the property is registered to the caller and keeps its history.
    /// </summary>
    [HttpPost("ownership-transfers")]
    [ProducesResponseType<AuthorityRequestView>(StatusCodes.Status201Created)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> CreateOwnershipTransfer(CreateAuthorityRequestRequest request, CancellationToken cancellationToken)
    {
        var details = new AuthorityRequestDetailsInput(
            request.Capacity, request.OwnershipDocumentNumber, request.BrokerageLicenseNumber, request.AgencyName, request.PropertyOwnerName);
        var result = await requests.CreateOwnershipTransferAsync(request.PropertyId ?? Guid.Empty, request.Purposes, details, cancellationToken);

        return result.IsFailure ? Fail(result.Error!) : StatusCode(StatusCodes.Status201Created, result.Value);
    }

    /// <summary>
    /// Saves the price and terms for one requested purpose (<c>Sale</c> or <c>Rent</c>), validated against the property
    /// now. <c>evidenceIds</c> is ignored: the request's own documents become the listing's evidence on approval.
    /// </summary>
    [HttpPut("{requestId:guid}/listings/{purpose}")]
    [ProducesResponseType<AuthorityRequestView>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> SetListing(
        Guid requestId,
        string purpose,
        SmartProperty.Api.Contracts.Marketplace.Owner.ListingDraftContentRequest? content,
        CancellationToken cancellationToken)
    {
        var input = SmartProperty.Api.Contracts.Marketplace.Owner.OwnerListingMapper.ToInput(content);

        if (input.IsFailure)
        {
            return Fail(input.Error!);
        }

        return ToResult(await requests.SetListingAsync(requestId, purpose, input.Value, cancellationToken));
    }

    [HttpPut("{requestId:guid}")]
    [ProducesResponseType<AuthorityRequestView>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> Update(Guid requestId, UpdateAuthorityRequestRequest request, CancellationToken cancellationToken)
    {
        var details = new AuthorityRequestDetailsInput(
            request.Capacity, request.OwnershipDocumentNumber, request.BrokerageLicenseNumber, request.AgencyName, request.PropertyOwnerName);

        return ToResult(await requests.UpdateDetailsAsync(requestId, details, cancellationToken));
    }

    [HttpGet]
    [ProducesResponseType<IReadOnlyList<AuthorityRequestView>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> List([FromQuery] Guid? propertyId, CancellationToken cancellationToken)
    {
        return ToResult(await requests.ListAsync(propertyId, cancellationToken));
    }

    [HttpGet("{requestId:guid}")]
    [ProducesResponseType<AuthorityRequestView>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Get(Guid requestId, CancellationToken cancellationToken)
    {
        return ToResult(await requests.GetAsync(requestId, cancellationToken));
    }

    /// <summary>
    /// Attaches one document to a draft: <c>multipart/form-data</c> with <c>file</c> (PDF, JPEG, PNG or WebP, at most
    /// 10 MB, type detected from the content) and <c>documentType</c>. 201 with the document: <c>documentId</c> and
    /// <c>scanStatus</c> PendingScan. The file is quarantined until the malware scan marks it Clean.
    /// </summary>
    [HttpPost("{requestId:guid}/documents")]
    [Consumes("multipart/form-data")]
    [RequestSizeLimit(MaxUploadBytes)]
    [RequestFormLimits(MultipartBodyLengthLimit = MaxUploadBytes)]
    [ProducesResponseType<AuthorityDocumentView>(StatusCodes.Status201Created)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> AddDocument(
        Guid requestId, IFormFile? file, [FromForm] string? documentType, CancellationToken cancellationToken)
    {
        if (file is null)
        {
            return Fail(AuthorityRequestErrors.Validation("A file is required in the 'file' field."));
        }

        await using var content = file.OpenReadStream();
        var result = await requests.AddDocumentAsync(
            requestId, new AuthorityDocumentUpload(documentType, file.FileName, content, file.Length), cancellationToken);

        return result.IsFailure ? Fail(result.Error!) : StatusCode(StatusCodes.Status201Created, result.Value);
    }

    /// <summary>
    /// Attaches several documents to a draft in one call: <c>multipart/form-data</c> with the files in the repeated
    /// <c>files</c> field and their types in the repeated <c>documentTypes</c> field, in the same order (or one
    /// <c>documentTypes</c> value for all the files). Each file has the same rules as the single upload (PDF, JPEG, PNG
    /// or WebP, at most 10 MB, type detected from the content), and a request carries at most 10 documents. All the files
    /// are added or none is. 201 with the documents, each <c>PendingScan</c> until its own malware scan.
    /// </summary>
    [HttpPost("{requestId:guid}/documents/batch")]
    [Consumes("multipart/form-data")]
    [RequestSizeLimit(MaxBatchUploadBytes)]
    [RequestFormLimits(MultipartBodyLengthLimit = MaxBatchUploadBytes)]
    [ProducesResponseType<IReadOnlyList<AuthorityDocumentView>>(StatusCodes.Status201Created)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> AddDocuments(
        Guid requestId,
        IFormFileCollection files,
        [FromForm] List<string?>? documentTypes,
        CancellationToken cancellationToken)
    {
        // IFormFileCollection (not List<IFormFile>) so the OpenAPI document describes multipart/form-data with file
        // pickers. It holds every file in the form; only the 'files' field is taken.
        var named = files.GetFiles("files");

        if (named.Count == 0 || named.Count != files.Count)
        {
            return Fail(AuthorityRequestErrors.Validation(named.Count == 0
                ? "At least one file is required in the 'files' field."
                : "Send every file in the 'files' field."));
        }

        var streams = new List<Stream>(named.Count);

        try
        {
            var uploads = new List<AuthorityDocumentUpload>(named.Count);

            foreach (var file in named)
            {
                var stream = file.OpenReadStream();
                streams.Add(stream);
                uploads.Add(new AuthorityDocumentUpload(null, file.FileName, stream, file.Length));
            }

            var result = await requests.AddDocumentsAsync(
                requestId, uploads, ExpandDocumentTypes(documentTypes), cancellationToken);

            return result.IsFailure ? Fail(result.Error!) : StatusCode(StatusCodes.Status201Created, result.Value);
        }
        finally
        {
            foreach (var stream in streams)
            {
                await stream.DisposeAsync();
            }
        }
    }

    /// <summary>
    /// The document types as sent: each form value is one type, a JSON array of types (<c>["TitleDeed"]</c>, which
    /// API clients such as Scalar send for array fields) or a comma-separated list. The result keeps the order.
    /// </summary>
    internal static IReadOnlyList<string?> ExpandDocumentTypes(IReadOnlyList<string?>? values)
    {
        var types = new List<string?>();

        foreach (var value in values ?? [])
        {
            var text = value?.Trim();

            if (text is ['[', .., ']'])
            {
                try
                {
                    types.AddRange(System.Text.Json.JsonSerializer.Deserialize<string?[]>(text) ?? []);
                    continue;
                }
                catch (System.Text.Json.JsonException)
                {
                    // Not a JSON array of strings: kept as it is, and refused as an unknown type.
                }
            }

            if (text is not null && text.Contains(','))
            {
                types.AddRange(text.Split(',', StringSplitOptions.TrimEntries));
                continue;
            }

            types.Add(text);
        }

        return types;
    }

    /// <summary>The documents in force; <c>includeHistory=true</c> lists every version ever stored.</summary>
    [HttpGet("{requestId:guid}/documents")]
    [ProducesResponseType<IReadOnlyList<AuthorityDocumentView>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> ListDocuments(Guid requestId, [FromQuery] bool includeHistory, CancellationToken cancellationToken)
    {
        return ToResult(await requests.ListDocumentsAsync(requestId, includeHistory, cancellationToken));
    }

    /// <summary>One document with all its versions.</summary>
    [HttpGet("{requestId:guid}/documents/{documentId:guid}")]
    [ProducesResponseType<AuthorityDocumentDetailView>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetDocument(Guid requestId, Guid documentId, CancellationToken cancellationToken)
    {
        return ToResult(await requests.GetDocumentAsync(requestId, documentId, cancellationToken));
    }

    /// <summary>
    /// Replaces a draft's document with a new file (<c>multipart/form-data</c>, field <c>file</c>); the type stays. The
    /// earlier version is kept. 201 with the new version, PendingScan.
    /// </summary>
    [HttpPut("{requestId:guid}/documents/{documentId:guid}")]
    [Consumes("multipart/form-data")]
    [RequestSizeLimit(MaxUploadBytes)]
    [RequestFormLimits(MultipartBodyLengthLimit = MaxUploadBytes)]
    [ProducesResponseType<AuthorityDocumentView>(StatusCodes.Status201Created)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> ReplaceDocument(Guid requestId, Guid documentId, IFormFile? file, CancellationToken cancellationToken)
    {
        if (file is null)
        {
            return Fail(AuthorityRequestErrors.Validation("A file is required in the 'file' field."));
        }

        await using var content = file.OpenReadStream();
        var result = await requests.ReplaceDocumentAsync(
            requestId, documentId, new AuthorityDocumentUpload(null, file.FileName, content, file.Length), cancellationToken);

        return result.IsFailure ? Fail(result.Error!) : StatusCode(StatusCodes.Status201Created, result.Value);
    }

    /// <summary>Removes a draft's document (its versions stay on record). Refused for a document that was part of an approval.</summary>
    [HttpDelete("{requestId:guid}/documents/{documentId:guid}")]
    [ProducesResponseType<AuthorityRequestView>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> RemoveDocument(Guid requestId, Guid documentId, CancellationToken cancellationToken)
    {
        return ToResult(await requests.RemoveDocumentAsync(requestId, documentId, cancellationToken));
    }

    /// <summary>Queues a document whose scan failed (ScanFailed) for a fresh set of attempts.</summary>
    [HttpPost("{requestId:guid}/documents/{documentId:guid}/rescan")]
    [ProducesResponseType<AuthorityDocumentView>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> RescanDocument(Guid requestId, Guid documentId, CancellationToken cancellationToken)
    {
        return ToResult(await requests.RescanDocumentAsync(requestId, documentId, cancellationToken));
    }

    /// <summary>Downloads the version in force. Only a Clean document is served (409 otherwise).</summary>
    [HttpGet("{requestId:guid}/documents/{documentId:guid}/file")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> OpenDocument(Guid requestId, Guid documentId, CancellationToken cancellationToken)
    {
        return FileResult(this, await requests.OpenDocumentAsync(requestId, documentId, null, cancellationToken));
    }

    /// <summary>Downloads one version. Only a Clean version is served.</summary>
    [HttpGet("{requestId:guid}/documents/{documentId:guid}/versions/{versionId:guid}/file")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> OpenDocumentVersion(Guid requestId, Guid documentId, Guid versionId, CancellationToken cancellationToken)
    {
        return FileResult(this, await requests.OpenDocumentAsync(requestId, documentId, versionId, cancellationToken));
    }

    /// <summary>
    /// Sends the draft to the legal reviewer. Needs everything in <c>missing</c> resolved, including every document
    /// scanned Clean.
    /// </summary>
    [HttpPost("{requestId:guid}/submit")]
    [ProducesResponseType<AuthorityRequestView>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> Submit(Guid requestId, CancellationToken cancellationToken)
    {
        return ToResult(await requests.SubmitAsync(requestId, cancellationToken));
    }

    [HttpPost("{requestId:guid}/withdraw")]
    [ProducesResponseType<AuthorityRequestView>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Withdraw(Guid requestId, CancellationToken cancellationToken)
    {
        return ToResult(await requests.WithdrawAsync(requestId, cancellationToken));
    }

    /// <summary>The signed-in user's grants. <c>state</c> is Active, Expired or Revoked.</summary>
    [HttpGet("~/api/marketplace/me/authority-grants")]
    [ProducesResponseType<IReadOnlyList<AuthorityGrantSummary>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> MyGrants([FromQuery] Guid? propertyId, CancellationToken cancellationToken)
    {
        return ToResult(await requests.ListGrantsAsync(propertyId, cancellationToken));
    }

    /// <summary>
    /// The history of a property the caller owns now: when its ownership changed, and every version that was published
    /// (title, description, price), including those of previous owners. Nothing private about a previous owner.
    /// </summary>
    [HttpGet("~/api/marketplace/me/properties/{propertyId:guid}/history")]
    [ProducesResponseType<PropertyHistoryView>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> PropertyHistory(Guid propertyId, CancellationToken cancellationToken)
    {
        return ToResult(await requests.GetPropertyHistoryAsync(propertyId, cancellationToken));
    }

    internal static IActionResult FileResult(ControllerBase controller, Result<AuthorityDocumentFile> result)
    {
        if (result.IsFailure)
        {
            var error = ApiErrorResponseFactory.FromError(result.Error!, controller.HttpContext);

            return controller.StatusCode(error.Status, error);
        }

        // Uploaded content is served back: the browser must not guess it is anything but the type it was checked as,
        // it is downloaded rather than rendered in the API's origin, and nothing caches it.
        controller.Response.Headers.XContentTypeOptions = "nosniff";
        controller.Response.Headers.CacheControl = "private, no-store";
        controller.Response.Headers.ContentSecurityPolicy = "default-src 'none'; sandbox";

        return controller.File(result.Value.Content, result.Value.ContentType, result.Value.FileName ?? "document");
    }

    private IActionResult ToResult<T>(Result<T> result) => result.IsFailure ? Fail(result.Error!) : Ok(result.Value);

    private IActionResult Fail(Error error)
    {
        var response = ApiErrorResponseFactory.FromError(error, HttpContext);

        return StatusCode(response.Status, response);
    }
}

/// <summary>
/// The staff side: the legal reviewer checks the documents and the property data, then approves, asks for completion
/// or rejects; a platform administrator, who must be a different person, then approves (issuing the grants and
/// publishing the listings atomically), asks for completion or rejects.
/// </summary>
/// <remarks>
/// Legal decisions need <c>marketplace.authority.legal_reviews.record</c> (the Legal Reviewer role); the final decision
/// needs <c>marketplace.authority.grants.issue</c> (the Platform Admin role); reading needs any authority permission.
/// Every decision needs <c>noConflictOfInterest: true</c>, and the reviewer's independence is checked against the
/// recorded relationships (see docs/authority-documents-and-review.md).
/// </remarks>
[ApiController]
[Authorize]
[Route("api/admin/marketplace/authority-requests")]
public sealed class AdminAuthorityRequestsController(StaffAuthorityRequests requests) : ControllerBase
{
    /// <summary>
    /// The queue. <c>status</c>: PendingLegalReview (the legal reviewer's work), PendingIssuance (the administrator's),
    /// Approved, Rejected or Withdrawn; omit for all. Waiting work is oldest first.
    /// </summary>
    [HttpGet]
    [ProducesResponseType<AuthorityRequestPage>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> List(
        [FromQuery] string? status, [FromQuery] int? page, [FromQuery] int? pageSize, CancellationToken cancellationToken)
    {
        return ToResult(await requests.ListAsync(status, page, pageSize, cancellationToken));
    }

    [HttpGet("{requestId:guid}")]
    [ProducesResponseType<AuthorityRequestView>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Get(Guid requestId, CancellationToken cancellationToken)
    {
        return ToResult(await requests.GetAsync(requestId, cancellationToken));
    }

    /// <summary>The property as registered now, to check against the documents.</summary>
    [HttpGet("{requestId:guid}/property")]
    [ProducesResponseType<PropertyDetails>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetProperty(Guid requestId, CancellationToken cancellationToken)
    {
        return ToResult(await requests.GetPropertyAsync(requestId, cancellationToken));
    }

    [HttpGet("{requestId:guid}/documents")]
    [ProducesResponseType<IReadOnlyList<AuthorityDocumentView>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> ListDocuments(Guid requestId, [FromQuery] bool includeHistory, CancellationToken cancellationToken)
    {
        return ToResult(await requests.ListDocumentsAsync(requestId, includeHistory, cancellationToken));
    }

    [HttpGet("{requestId:guid}/documents/{documentId:guid}")]
    [ProducesResponseType<AuthorityDocumentDetailView>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetDocument(Guid requestId, Guid documentId, CancellationToken cancellationToken)
    {
        return ToResult(await requests.GetDocumentAsync(requestId, documentId, cancellationToken));
    }

    [HttpGet("{requestId:guid}/documents/{documentId:guid}/file")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> OpenDocument(Guid requestId, Guid documentId, CancellationToken cancellationToken)
    {
        return AuthorityRequestsController.FileResult(this, await requests.OpenDocumentAsync(requestId, documentId, null, cancellationToken));
    }

    [HttpGet("{requestId:guid}/documents/{documentId:guid}/versions/{versionId:guid}/file")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> OpenDocumentVersion(Guid requestId, Guid documentId, Guid versionId, CancellationToken cancellationToken)
    {
        return AuthorityRequestsController.FileResult(this, await requests.OpenDocumentAsync(requestId, documentId, versionId, cancellationToken));
    }

    /// <summary>The legal reviewer approves. Moves the request to PendingIssuance; nothing is published.</summary>
    [HttpPost("{requestId:guid}/legal-approve")]
    [ProducesResponseType<AuthorityRequestView>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> ApproveLegalReview(Guid requestId, LegalApprovalRequest? request, CancellationToken cancellationToken)
    {
        return ToResult(await requests.ApproveLegalReviewAsync(requestId, request?.Note, request?.NoConflictOfInterest, cancellationToken));
    }

    /// <summary>
    /// The administrator's final approval (must not be the legal reviewer): re-checks every requirement, issues the
    /// grants and publishes the listings in one transaction. 200 with status Approved and the grant ids.
    /// </summary>
    [HttpPost("{requestId:guid}/issue")]
    [ProducesResponseType<AuthorityRequestView>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> Issue(Guid requestId, IssueGrantsRequest? request, CancellationToken cancellationToken)
    {
        return ToResult(await requests.IssueAsync(requestId, request?.ValidDays, request?.NoConflictOfInterest, cancellationToken));
    }

    /// <summary>
    /// Sends the request back to the requester to complete (at legal review or at the final decision). The reason is
    /// shown to them; after they resubmit, both reviews are done again.
    /// </summary>
    [HttpPost("{requestId:guid}/request-completion")]
    [ProducesResponseType<AuthorityRequestView>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> RequestCompletion(Guid requestId, AuthorityDecisionRequest? request, CancellationToken cancellationToken)
    {
        return ToResult(await requests.RequestCompletionAsync(requestId, request?.Reason, request?.NoConflictOfInterest, cancellationToken));
    }

    /// <summary>Rejects the request for a reason. Final.</summary>
    [HttpPost("{requestId:guid}/reject")]
    [ProducesResponseType<AuthorityRequestView>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> Reject(Guid requestId, AuthorityDecisionRequest? request, CancellationToken cancellationToken)
    {
        return ToResult(await requests.RejectAsync(requestId, request?.Reason, request?.NoConflictOfInterest, cancellationToken));
    }

    private IActionResult ToResult<T>(Result<T> result)
    {
        if (!result.IsFailure)
        {
            return Ok(result.Value);
        }

        var response = ApiErrorResponseFactory.FromError(result.Error!, HttpContext);

        return StatusCode(response.Status, response);
    }
}

/// <summary>Advertising grants: list them, and revoke one (<c>marketplace.authority.grants.revoke</c>).</summary>
[ApiController]
[Authorize]
[Route("api/admin/marketplace/authority-grants")]
public sealed class AdminAuthorityGrantsController(StaffAuthorityRequests requests) : ControllerBase
{
    [HttpGet]
    [ProducesResponseType<IReadOnlyList<AuthorityGrantSummary>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> List(
        [FromQuery] Guid? propertyId, [FromQuery] Guid? ownerUserId, CancellationToken cancellationToken)
    {
        return ToResult(await requests.ListGrantsAsync(propertyId, ownerUserId, cancellationToken));
    }

    /// <summary>
    /// Revokes the grant permanently; <c>expectedVersion</c> is the grant's current <c>version</c>. Also the way to clear
    /// an expired grant so a new request can be issued.
    /// </summary>
    [HttpPost("{grantId:guid}/revoke")]
    [ProducesResponseType<AuthorityGrantSummary>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Revoke(Guid grantId, RevokeGrantRequest? request, CancellationToken cancellationToken)
    {
        return ToResult(await requests.RevokeGrantAsync(grantId, request?.ExpectedVersion, cancellationToken));
    }

    private IActionResult ToResult<T>(Result<T> result)
    {
        if (!result.IsFailure)
        {
            return Ok(result.Value);
        }

        var response = ApiErrorResponseFactory.FromError(result.Error!, HttpContext);

        return StatusCode(response.Status, response);
    }
}
