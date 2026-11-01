#nullable enable
using Microsoft.AspNetCore.Mvc;
using SmartProperty.Api.Contracts;
using SmartProperty.Api.Contracts.PropertyMatching;
using SmartProperty.Api.Infrastructure.Authentication;
using SmartProperty.Api.Infrastructure.Errors;
using SmartProperty.Application.Abstractions.Messaging;
using SmartProperty.Application.PropertyMatching.Record;

namespace SmartProperty.Api.Controllers;

/// <summary>
/// Receives the results of the external AI matching system.
/// </summary>
/// <remarks>
/// Called by the AI system, not by a signed-in user, so it is authenticated by the shared <c>X-Api-Key</c> rather
/// than a JWT. The AI system calls it once per matched property (up to three per search).
/// </remarks>
[ApiController]
[Route("api/property-matches")]
public sealed class PropertyMatchesController : ControllerBase
{
    [HttpPost]
    [RequireAiApiKey]
    [ProducesResponseType<PropertyMatchResponse>(StatusCodes.Status201Created)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> Record(
        RecordPropertyMatchRequest request,
        [FromServices] ICommandHandler<RecordPropertyMatchCommand, PropertyMatchDetails> handler,
        CancellationToken cancellationToken)
    {
        var result = await handler.Handle(
            new RecordPropertyMatchCommand(
                request.UserId,
                request.PropertyId,
                request.MatchPercentage,
                request.Description,
                request.Query),
            cancellationToken);

        if (result.IsFailure)
        {
            var errorResponse = ApiErrorResponseFactory.FromError(result.Error!, HttpContext);
            return StatusCode(errorResponse.Status, errorResponse);
        }

        var match = result.Value;
        return StatusCode(StatusCodes.Status201Created, new PropertyMatchResponse(
            match.Id,
            match.UserId,
            match.PropertyId,
            match.MatchPercentage,
            match.Description,
            match.Query,
            match.CreatedAt));
    }
}
