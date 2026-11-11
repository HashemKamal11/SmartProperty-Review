#nullable enable
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SmartProperty.Api.Contracts;
using SmartProperty.Api.Contracts.Marketplace.Owner;
using SmartProperty.Api.Infrastructure.Errors;
using SmartProperty.Application.Abstractions.Messaging;
using SmartProperty.Application.Marketplace.ListingSubmissions.Write;
using SmartProperty.Application.Marketplace.ListingSubmissions.Write.Create;
using SmartProperty.Application.Marketplace.ListingSubmissions.Write.CreateBoth;
using SmartProperty.Application.Marketplace.ListingSubmissions.Write.Discard;
using SmartProperty.Application.Marketplace.ListingSubmissions.Write.Submit;
using SmartProperty.Application.Marketplace.ListingSubmissions.Write.Update;
using SmartProperty.Application.Marketplace.ListingSubmissions.Write.Withdraw;
using SmartProperty.Common.Results;

namespace SmartProperty.Api.Controllers;

/// <summary>
/// The authenticated owner's advertisement workflow: draft, edit, submit, discard and withdraw.
/// </summary>
/// <remarks>
/// <para>
/// <b>Who acts.</b> Always the authenticated principal, whose persisted account must be Active. No request carries a
/// user, owner or authority, and any such member is ignored.
/// </para>
/// <para>
/// <b>Authority.</b> Every write is allowed only for the purpose a valid, administrator-issued grant names. Advertising
/// for both sale and rent needs a Sale grant and a Rent grant; if either is missing nothing is written. A caller
/// without the grant, and one asking about a property that does not exist, get the same <c>403</c>. Issuing grants is
/// not part of this API.
/// </para>
/// <para>
/// <b>Publication.</b> Nothing here publishes. A submitted draft is reviewed by an administrator, and only an approval
/// publishes it. Evidence is verified by a trusted provider; while none is configured, attaching evidence, submitting
/// and publishing answer <c>503 listing_submissions.evidence_verification_unavailable</c>.
/// </para>
/// <para>
/// <b>Re-advertising.</b> A withdrawn, rejected or superseded listing is advertised again by creating a new draft for
/// the same property and purpose, submitting it and having it approved; a withdrawn listing is never republished from
/// anything older than the withdrawal.
/// </para>
/// </remarks>
[ApiController]
[Authorize]
[Route("api/marketplace/me")]
public sealed class OwnerListingsController : ControllerBase
{
    /// <summary>Creates a draft for one property and one purpose, or the next version of a reviewed one.</summary>
    [HttpPost("listing-submissions")]
    [ProducesResponseType<ListingDraftResponse>(StatusCodes.Status201Created)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status422UnprocessableEntity)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> CreateDraft(
        CreateListingDraftRequest request,
        [FromServices] ICommandHandler<CreateListingDraftCommand, ListingDraftResult> handler,
        CancellationToken cancellationToken)
    {
        var purpose = OwnerListingMapper.ParsePurpose(request.Purpose);
        var content = OwnerListingMapper.ToInput(request.Content);

        if (request.PropertyId is not { } propertyId || propertyId == Guid.Empty)
        {
            return Fail(OwnerListingMapper.Invalid("The property is required."));
        }

        if (purpose.IsFailure)
        {
            return Fail(purpose.Error!);
        }

        if (content.IsFailure)
        {
            return Fail(content.Error!);
        }

        var result = await handler.Handle(
            new CreateListingDraftCommand(propertyId, purpose.Value, content.Value), cancellationToken);

        return result.IsFailure
            ? Fail(result.Error!)
            : StatusCode(StatusCodes.Status201Created, OwnerListingMapper.ToResponse(result.Value));
    }

    /// <summary>
    /// Creates drafts that advertise one property for sale, for rent, or for both, each with its own terms.
    /// </summary>
    /// <remarks>
    /// "Both" is two purpose-specific drafts created in one unit of work: both exist or neither does. Each is then
    /// submitted, reviewed and published on its own.
    /// </remarks>
    [HttpPost("properties/{propertyId:guid}/listing-drafts")]
    [ProducesResponseType<PropertyListingDraftsResponse>(StatusCodes.Status201Created)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status422UnprocessableEntity)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> CreateDrafts(
        Guid propertyId,
        CreatePropertyListingDraftsRequest request,
        [FromServices] ICommandHandler<CreatePropertyListingDraftsCommand, PropertyListingDraftsResult> handler,
        CancellationToken cancellationToken)
    {
        ListingDraftContentInput? sale = null;
        ListingDraftContentInput? rent = null;

        if (request.Sale is not null)
        {
            var parsed = OwnerListingMapper.ToInput(request.Sale);

            if (parsed.IsFailure)
            {
                return Fail(parsed.Error!);
            }

            sale = parsed.Value;
        }

        if (request.Rent is not null)
        {
            var parsed = OwnerListingMapper.ToInput(request.Rent);

            if (parsed.IsFailure)
            {
                return Fail(parsed.Error!);
            }

            rent = parsed.Value;
        }

        var result = await handler.Handle(new CreatePropertyListingDraftsCommand(propertyId, sale, rent), cancellationToken);

        if (result.IsFailure)
        {
            return Fail(result.Error!);
        }

        return StatusCode(
            StatusCodes.Status201Created,
            new PropertyListingDraftsResponse(
                result.Value.Sale is { } s ? OwnerListingMapper.ToResponse(s) : null,
                result.Value.Rent is { } r ? OwnerListingMapper.ToResponse(r) : null));
    }

    /// <summary>Replaces the content of a never-submitted draft. The revision must be the one last read.</summary>
    [HttpPut("listing-submissions/{submissionId:guid}")]
    [ProducesResponseType<ListingDraftResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status422UnprocessableEntity)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> UpdateDraft(
        Guid submissionId,
        UpdateListingDraftRequest request,
        [FromServices] ICommandHandler<UpdateListingDraftCommand, ListingDraftResult> handler,
        CancellationToken cancellationToken)
    {
        if (request.ExpectedRevision is not { } revision)
        {
            return Fail(OwnerListingMapper.Invalid("The expected revision is required."));
        }

        var content = OwnerListingMapper.ToInput(request.Content);

        if (content.IsFailure)
        {
            return Fail(content.Error!);
        }

        var result = await handler.Handle(
            new UpdateListingDraftCommand(submissionId, revision, content.Value), cancellationToken);

        return result.IsFailure ? Fail(result.Error!) : Ok(OwnerListingMapper.ToResponse(result.Value));
    }

    /// <summary>Submits the caller's own draft for administrator review. It does not publish.</summary>
    [HttpPost("listing-submissions/{submissionId:guid}/submit")]
    [ProducesResponseType<SubmitListingDraftResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> Submit(
        Guid submissionId,
        SubmitListingDraftRequest request,
        [FromServices] ICommandHandler<SubmitListingDraftCommand, SubmitListingDraftResult> handler,
        CancellationToken cancellationToken)
    {
        if (request.ExpectedRevision is not { } revision)
        {
            return Fail(OwnerListingMapper.Invalid("The expected revision is required."));
        }

        var result = await handler.Handle(new SubmitListingDraftCommand(submissionId, revision), cancellationToken);

        return result.IsFailure
            ? Fail(result.Error!)
            : Ok(new SubmitListingDraftResponse(result.Value.SubmissionId, result.Value.Status.ToString(), result.Value.SubmittedAt));
    }

    /// <summary>Discards a never-submitted draft. Submitted versions and history are never removed.</summary>
    [HttpDelete("listing-submissions/{submissionId:guid}")]
    [ProducesResponseType<DiscardListingDraftResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Discard(
        Guid submissionId,
        [FromQuery] int? expectedRevision,
        [FromServices] ICommandHandler<DiscardListingDraftCommand, DiscardListingDraftResult> handler,
        CancellationToken cancellationToken)
    {
        if (expectedRevision is not { } revision)
        {
            return Fail(OwnerListingMapper.Invalid("The expected revision is required."));
        }

        var result = await handler.Handle(new DiscardListingDraftCommand(submissionId, revision), cancellationToken);

        return result.IsFailure ? Fail(result.Error!) : Ok(new DiscardListingDraftResponse(result.Value.ListingRemoved));
    }

    /// <summary>Withdraws the caller's own published listing. Only that purpose is affected.</summary>
    [HttpPost("listings/{listingId:guid}/withdraw")]
    [ProducesResponseType<WithdrawListingResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Withdraw(
        Guid listingId,
        [FromServices] ICommandHandler<WithdrawListingCommand, WithdrawListingResult> handler,
        CancellationToken cancellationToken)
    {
        var result = await handler.Handle(new WithdrawListingCommand(listingId), cancellationToken);

        return result.IsFailure
            ? Fail(result.Error!)
            : Ok(new WithdrawListingResponse(result.Value.ListingId, result.Value.WithdrawnAt));
    }

    private IActionResult Fail(Error error)
    {
        var response = ApiErrorResponseFactory.FromError(error, HttpContext);

        return StatusCode(response.Status, response);
    }
}
