#nullable enable
using Microsoft.AspNetCore.Mvc;
using SmartProperty.Api.Contracts;
using SmartProperty.Api.Contracts.OwnerVerification;
using SmartProperty.Api.Infrastructure.Authorization;
using SmartProperty.Api.Infrastructure.Errors;
using SmartProperty.Application.Abstractions.Identity;
using SmartProperty.Application.Abstractions.Messaging;
using SmartProperty.Application.Authorization;
using SmartProperty.Application.OwnerVerification;
using SmartProperty.Application.OwnerVerification.Get;
using SmartProperty.Application.OwnerVerification.Review;
using SmartProperty.Common.Pagination;
using SmartProperty.Common.Results;
using SmartProperty.Domain.OwnerVerification;

namespace SmartProperty.Api.Controllers;

/// <summary>
/// The reviewers' side of owner verification: read the submitted profiles and their evidence, approve or reject.
/// </summary>
/// <remarks>
/// Requires <c>owner.verification.review</c>. Approving grants the owner the "Property Owner" role, which is what lets
/// them add properties. Profiles still in <c>Draft</c> are invisible here, and nobody can decide their own profile.
/// </remarks>
[ApiController]
[Route("api/admin/owner-verifications")]
[RequirePermission(PermissionCodes.OwnerVerificationReview)]
public sealed class AdminOwnerVerificationsController : ControllerBase
{
    /// <summary>One page of submitted profiles. Filter by <c>status</c> (UnderReview, Approved, Rejected).</summary>
    [HttpGet]
    [ProducesResponseType<PagedResponse<OwnerReviewItemResponse>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> List(
        [FromServices] IQueryHandler<GetOwnerReviewQueueQuery, PagedList<OwnerReviewItem>> handler,
        CancellationToken cancellationToken,
        [FromQuery] OwnerVerificationStatus? status = null,
        [FromQuery] int? page = null,
        [FromQuery] int? pageSize = null)
    {
        var result = await handler.Handle(new GetOwnerReviewQueueQuery(status, page, pageSize), cancellationToken);

        if (result.IsFailure)
        {
            var errorResponse = ApiErrorResponseFactory.FromError(result.Error!, HttpContext);
            return StatusCode(errorResponse.Status, errorResponse);
        }

        var paged = result.Value;
        var items = paged.Items
            .Select(item => new OwnerReviewItemResponse(
                item.ProfileId,
                item.UserId,
                item.FirstName,
                item.LastName,
                item.Email,
                item.PhoneNumber,
                item.Status.ToString(),
                item.SubmittedAt,
                item.ReviewedAt,
                item.ReviewedByUserId))
            .ToArray();

        return Ok(PagedResponse<OwnerReviewItemResponse>.FromPagedList(
            PagedList<OwnerReviewItemResponse>.Create(items, paged.PageNumber, paged.PageSize, paged.TotalCount)));
    }

    /// <summary>A submitted profile with all three steps.</summary>
    [HttpGet("{id:guid}")]
    [ProducesResponseType<OwnerProfileResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Get(
        Guid id,
        [FromServices] IQueryHandler<GetOwnerProfileForReviewQuery, OwnerProfileDetails> handler,
        CancellationToken cancellationToken)
    {
        return ToResult(await handler.Handle(new GetOwnerProfileForReviewQuery(id), cancellationToken));
    }

    /// <summary>The address proof image of a submitted profile.</summary>
    [HttpGet("{id:guid}/address-proof")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetAddressProof(
        Guid id,
        [FromServices] IQueryHandler<GetAddressProofForReviewQuery, AddressProofFile> handler,
        CancellationToken cancellationToken)
    {
        var result = await handler.Handle(new GetAddressProofForReviewQuery(id), cancellationToken);

        if (result.IsFailure)
        {
            var errorResponse = ApiErrorResponseFactory.FromError(result.Error!, HttpContext);
            return StatusCode(errorResponse.Status, errorResponse);
        }

        Response.Headers.CacheControl = "private, no-store";
        Response.Headers.XContentTypeOptions = "nosniff";

        return File(result.Value.Content, result.Value.ContentType);
    }

    /// <summary>Approves a profile under review and lets its owner add properties.</summary>
    [HttpPost("{id:guid}/approve")]
    [ProducesResponseType<OwnerProfileResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Approve(
        Guid id,
        [FromServices] ICurrentUser currentUser,
        [FromServices] ICommandHandler<ApproveOwnerProfileCommand, OwnerProfileDetails> handler,
        CancellationToken cancellationToken)
    {
        if (currentUser.UserId is not { } reviewerId)
        {
            return Unauthorized(ApiErrorResponseFactory.Unauthorized(HttpContext));
        }

        return ToResult(await handler.Handle(new ApproveOwnerProfileCommand(id, reviewerId), cancellationToken));
    }

    /// <summary>Rejects a profile under review. The reason is shown to the owner, who corrects and resubmits.</summary>
    [HttpPost("{id:guid}/reject")]
    [ProducesResponseType<OwnerProfileResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> Reject(
        Guid id,
        RejectOwnerProfileRequest request,
        [FromServices] ICurrentUser currentUser,
        [FromServices] ICommandHandler<RejectOwnerProfileCommand, OwnerProfileDetails> handler,
        CancellationToken cancellationToken)
    {
        if (currentUser.UserId is not { } reviewerId)
        {
            return Unauthorized(ApiErrorResponseFactory.Unauthorized(HttpContext));
        }

        return ToResult(await handler.Handle(
            new RejectOwnerProfileCommand(id, reviewerId, request.Reason),
            cancellationToken));
    }

    private IActionResult ToResult(Result<OwnerProfileDetails> result)
    {
        if (result.IsFailure)
        {
            var errorResponse = ApiErrorResponseFactory.FromError(result.Error!, HttpContext);
            return StatusCode(errorResponse.Status, errorResponse);
        }

        return Ok(OwnerProfileResponse.From(result.Value));
    }
}
