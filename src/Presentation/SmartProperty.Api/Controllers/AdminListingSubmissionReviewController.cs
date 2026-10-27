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
/// <b>This controller is deliberately not active.</b> It is <c>internal</c>, and MVC discovers only public
/// controllers, so the production host exposes no route for it. The handlers it needs are not registered, the
/// <c>IReviewReadAuthorizer</c> adapter is not registered, and the read repository behind them,
/// <c>IReviewerListingSubmissionReadRepository</c>, has no production implementation yet; a discoverable route would
/// answer every request with a server error. Tests mount it explicitly, with a test-only repository stub. To activate
/// it once the repository exists: make this class <c>public</c>, register the two handlers and
/// <c>PlatformReviewReadAuthorizer</c> beside the other marketplace services, register the repository in the
/// persistence composition, and turn the "no route exists" gate tests into "the route exists" tests.
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
/// <c>ICurrentUser</c> alone and hand that id to the repository, which uses it only to establish independence.
/// </para>
/// <para>
/// <b>Independence.</b> A reviewer who is the submitter, the declared owner or affiliated with the submitting
/// Workspace, or whose independence cannot be established, is refused the detail with a <c>403</c>. The repository
/// decides; this controller adds no rule of its own. A missing submission is a <c>404</c>.
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
internal sealed class AdminListingSubmissionReviewController : ControllerBase
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
    /// A reviewer who is not independent of the submission, or whose independence cannot be established, is a
    /// <c>403</c>. A submission that does not exist is a <c>404 listing_submissions.not_found</c>. A value that is not
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
