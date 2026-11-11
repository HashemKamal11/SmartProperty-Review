#nullable enable
using Microsoft.AspNetCore.Mvc;
using SmartProperty.Api.Contracts;
using SmartProperty.Api.Infrastructure.Authorization;
using SmartProperty.Api.Infrastructure.Errors;
using SmartProperty.Application.Abstractions.Messaging;
using SmartProperty.Application.Authorization;
using SmartProperty.Application.Marketplace.ListingSubmissions.Review.Decide;

namespace SmartProperty.Api.Controllers;

/// <summary>The body of a rejection: a reason written for the submitter.</summary>
public sealed record RejectListingSubmissionRequest(string? Reason);

/// <summary>The outcome of a decision. It carries no reason and no reviewer.</summary>
public sealed record ReviewDecisionResultResponse(Guid SubmissionId, Guid ListingId, string Status, DateTimeOffset DecidedAt);

/// <summary>
/// Approving and rejecting a submitted listing.
/// </summary>
/// <remarks>
/// <para>
/// Requires the Platform-scoped <c>marketplace.submissions.review.decide</c> permission at the door and again in the
/// handler. It is deliberately a separate controller from the read-only review controller: reading the queue and
/// deciding are independent authorities, and a controller-level read requirement would be added to every action here.
/// </para>
/// <para>
/// A decision also needs an independent reviewer, established from trusted facts and never from the request. While no
/// independence provider is configured every decision answers <c>403 listing_submissions.independence_unverifiable</c>,
/// and while no evidence provider is configured an approval answers <c>503</c>; a rejection needs no evidence.
/// </para>
/// </remarks>
[ApiController]
[RequirePermission(PermissionCodes.MarketplaceSubmissionsReviewDecide)]
[Route("api/admin/marketplace/listing-submissions")]
public sealed class AdminListingSubmissionDecisionsController : ControllerBase
{
    [HttpPost("{submissionId:guid}/approve")]
    [ProducesResponseType<ReviewDecisionResultResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> Approve(
        Guid submissionId,
        [FromServices] ICommandHandler<ApproveListingSubmissionCommand, ReviewDecisionResult> handler,
        CancellationToken cancellationToken)
    {
        var result = await handler.Handle(new ApproveListingSubmissionCommand(submissionId), cancellationToken);

        return Respond(result);
    }

    [HttpPost("{submissionId:guid}/reject")]
    [ProducesResponseType<ReviewDecisionResultResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> Reject(
        Guid submissionId,
        RejectListingSubmissionRequest request,
        [FromServices] ICommandHandler<RejectListingSubmissionCommand, ReviewDecisionResult> handler,
        CancellationToken cancellationToken)
    {
        var result = await handler.Handle(
            new RejectListingSubmissionCommand(submissionId, request.Reason ?? string.Empty), cancellationToken);

        return Respond(result);
    }

    private IActionResult Respond(SmartProperty.Common.Results.Result<ReviewDecisionResult> result)
    {
        if (result.IsFailure)
        {
            var response = ApiErrorResponseFactory.FromError(result.Error!, HttpContext);

            return StatusCode(response.Status, response);
        }

        var decision = result.Value;

        return Ok(new ReviewDecisionResultResponse(
            decision.SubmissionId, decision.ListingId, decision.Status.ToString(), decision.DecidedAt));
    }
}
