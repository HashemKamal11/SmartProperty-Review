#nullable enable
using Microsoft.AspNetCore.Mvc;
using SmartProperty.Api.Contracts;
using SmartProperty.Api.Contracts.Marketplace.Admin;
using SmartProperty.Api.Infrastructure.Authorization;
using SmartProperty.Api.Infrastructure.Errors;
using SmartProperty.Application.Abstractions.Messaging;
using SmartProperty.Application.Authorization;
using SmartProperty.Application.Marketplace.ListingSubmissions.Review;
using SmartProperty.Application.Marketplace.ListingSubmissions.Review.Detail;
using SmartProperty.Application.Marketplace.ListingSubmissions.Review.Queue;
using SmartProperty.Common.Pagination;
using SmartProperty.Domain.Marketplace;

namespace SmartProperty.Api.Controllers;

/// <summary>
/// The Platform Admin review reads: the queue of listing submissions awaiting review, and one submission in full.
/// </summary>
/// <remarks>
/// <para>
/// <b>Read-only, and active.</b> This controller exposes the two Platform Admin review reads and nothing else. It
/// has no write action. No Approve/Reject controller, decision handler or decision authorizer exists in this build,
/// so no decision route exists. The
/// handlers are registered with <c>PlatformReviewReadAuthorizer</c> beside the other marketplace services, and the
/// read repository is the PostgreSQL one registered by Persistence.
/// </para>
/// <para>
/// <b>Who may read.</b> Authentication and the Platform-scoped <see cref="PermissionCodes.MarketplaceSubmissionsReviewRead"/>
/// permission, checked at the door by <see cref="RequirePermissionAttribute"/> and again by each handler through
/// <c>IReviewReadAuthorizer</c>, so neither layer is relied on alone. Both ask the one permission checker about the
/// authenticated user; neither reads a role name, a Workspace membership, a claim or any request value. The authority
/// to decide is a separate permission and is never consulted here.
/// </para>
/// <para>
/// <b>Who is reviewing.</b> Never a request value. The routes and queries have no reviewer, user, owner or Workspace
/// parameter, and a client-supplied one is ignored like any unknown query key. The handlers read the reviewer from
/// <c>ICurrentUser</c> alone, for authorization; the repository receives no identity.
/// </para>
/// <para>
/// <b>Independence.</b> Reading does not depend on it. An authorized reader sees the queue and the redacted detail
/// whoever submitted or owns the submission; independence remains mandatory for any future decision operation. The
/// detail never carries the rejection reason. A missing submission is a <c>404</c>; a <c>403</c> means the caller
/// lacks the read permission.
/// </para>
/// <para>
/// The response contracts are the reviewer allowlists. An unparseable enum, number or page value is a
/// <c>400 request.malformed</c> from model binding, and a corrupt row is a generic <c>500</c> from the mapper that
/// discloses nothing.
/// </para>
/// </remarks>
[ApiController]
[RequirePermission(PermissionCodes.MarketplaceSubmissionsReviewRead)]
[Route("api/admin/marketplace/listing-submissions")]
public sealed class AdminListingSubmissionReviewController : ControllerBase
{
    /// <summary>
    /// One page of the review queue, optionally narrowed by status and purpose.
    /// </summary>
    /// <remarks>
    /// Without a status the queue is the submissions awaiting review, oldest submitted first; any other status is
    /// most recently updated first. A Draft is never reviewable and is refused.
    /// </remarks>
    [HttpGet]
    [ProducesResponseType<PagedResponse<ReviewQueueItemResponse>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> List(
        [FromServices] IQueryHandler<GetReviewQueueQuery, PagedList<ReviewQueueItem>> handler,
        CancellationToken cancellationToken,
        [FromQuery] ListingSubmissionStatus? status = null,
        [FromQuery] ListingPurpose? purpose = null,
        [FromQuery] int? page = null,
        [FromQuery] int? pageSize = null)
    {
        var result = await handler.Handle(
            new GetReviewQueueQuery(status, purpose, page, pageSize),
            cancellationToken);

        if (result.IsFailure)
        {
            var errorResponse = ApiErrorResponseFactory.FromError(result.Error!, HttpContext);

            return StatusCode(errorResponse.Status, errorResponse);
        }

        return Ok(AdminReviewMapper.ToPagedResponse(result.Value));
    }

    /// <summary>
    /// One submission for review, by its identifier.
    /// </summary>
    /// <remarks>
    /// A caller without the read permission is a <c>403</c>. A submission that does not exist is a <c>404 listing_submissions.not_found</c>. A value that is not
    /// a GUID matches no route at all.
    /// </remarks>
    [HttpGet("{submissionId:guid}")]
    [ProducesResponseType<ReviewSubmissionDetailResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetById(
        Guid submissionId,
        [FromServices] IQueryHandler<GetReviewSubmissionQuery, ReviewSubmissionDetail> handler,
        CancellationToken cancellationToken)
    {
        var result = await handler.Handle(new GetReviewSubmissionQuery(submissionId), cancellationToken);

        if (result.IsFailure)
        {
            var errorResponse = ApiErrorResponseFactory.FromError(result.Error!, HttpContext);

            return StatusCode(errorResponse.Status, errorResponse);
        }

        return Ok(AdminReviewMapper.ToDetailResponse(result.Value));
    }
}
