#nullable enable
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SmartProperty.Api.Contracts;
using SmartProperty.Api.Contracts.Marketplace.Submitter;
using SmartProperty.Api.Infrastructure.Errors;
using SmartProperty.Application.Abstractions.Messaging;
using SmartProperty.Application.Marketplace.ListingSubmissions;
using SmartProperty.Application.Marketplace.ListingSubmissions.GetById;
using SmartProperty.Application.Marketplace.ListingSubmissions.Mine;
using SmartProperty.Common.Pagination;
using SmartProperty.Domain.Marketplace;

namespace SmartProperty.Api.Controllers;

/// <summary>
/// The authenticated user's own listing submissions: a paged list and one submission in full.
/// </summary>
/// <remarks>
/// <para>
/// <b>This controller is deliberately not active.</b> It is <c>internal</c>, and MVC discovers only public
/// controllers, so the production host exposes no route for it. The handlers it needs are not registered and the
/// read repository behind them, <c>ISubmitterListingSubmissionReadRepository</c>, has no production implementation
/// yet; a discoverable route would answer every request with a server error. Tests mount it explicitly, with a
/// test-only repository stub. To activate it once the repository exists: make this class <c>public</c>, register the
/// two handlers beside the other marketplace handlers, register the repository in the persistence composition, and
/// turn the "no route exists" gate tests into "the route exists" tests.
/// </para>
/// <para>
/// <b>Whose submissions.</b> Never a request value. The route and the query have no user or submitter parameter, and
/// a client-supplied one is ignored like any unknown query key. The handlers read the acting user from
/// <c>ICurrentUser</c> alone, and an unusable subject is a <c>401</c> before anything is read.
/// </para>
/// <para>
/// <b>Another user's submission and a missing one are the same <c>404</c>.</b> The repository returns nothing for
/// both, so the response cannot tell them apart. The response contracts are the submitter allowlists: no property
/// id, legal party, user, Workspace, evidence or reviewer detail, and an approval never carries a reason.
/// </para>
/// <para>
/// An unparseable enum, number or page value is a <c>400 request.malformed</c> from model binding. A corrupt row is a
/// generic <c>500</c> from the mapper that discloses nothing.
/// </para>
/// </remarks>
[ApiController]
[Authorize]
[Route("api/marketplace/me/listing-submissions")]
internal sealed class SubmitterListingSubmissionsController : ControllerBase
{
    /// <summary>
    /// One page of the authenticated user's own submissions, newest first, optionally narrowed by purpose and status.
    /// </summary>
    [HttpGet]
    [ProducesResponseType<PagedResponse<SubmitterSubmissionSummaryResponse>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> List(
        [FromServices] IQueryHandler<GetMyListingSubmissionsQuery, PagedList<SubmitterSubmissionSummary>> handler,
        CancellationToken cancellationToken,
        [FromQuery] ListingPurpose? purpose = null,
        [FromQuery] ListingSubmissionStatus? status = null,
        [FromQuery] int? page = null,
        [FromQuery] int? pageSize = null)
    {
        var result = await handler.Handle(
            new GetMyListingSubmissionsQuery(purpose, status, page, pageSize),
            cancellationToken);

        if (result.IsFailure)
        {
            var errorResponse = ApiErrorResponseFactory.FromError(result.Error!, HttpContext);

            return StatusCode(errorResponse.Status, errorResponse);
        }

        return Ok(SubmitterSubmissionMapper.ToPagedResponse(result.Value));
    }

    /// <summary>
    /// One of the authenticated user's own submissions by its identifier.
    /// </summary>
    /// <remarks>
    /// A submission that is missing and one that belongs to another user are the same
    /// <c>404 listing_submissions.not_found</c>. A value that is not a GUID matches no route at all.
    /// </remarks>
    [HttpGet("{submissionId:guid}")]
    [ProducesResponseType<SubmitterSubmissionDetailResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetById(
        Guid submissionId,
        [FromServices] IQueryHandler<GetMyListingSubmissionByIdQuery, SubmitterSubmissionDetail> handler,
        CancellationToken cancellationToken)
    {
        var result = await handler.Handle(new GetMyListingSubmissionByIdQuery(submissionId), cancellationToken);

        if (result.IsFailure)
        {
            var errorResponse = ApiErrorResponseFactory.FromError(result.Error!, HttpContext);

            return StatusCode(errorResponse.Status, errorResponse);
        }

        return Ok(SubmitterSubmissionMapper.ToDetailResponse(result.Value));
    }
}
