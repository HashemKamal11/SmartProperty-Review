#nullable enable
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SmartProperty.Api.Contracts;
using SmartProperty.Api.Infrastructure.Development;
using SmartProperty.Application.Abstractions.Identity;
using SmartProperty.Application.Abstractions.Persistence.Repositories;
using SmartProperty.Application.Abstractions.Time;

namespace SmartProperty.Api.Controllers;

/// <summary>What the fixture endpoint returns. It always says the fixture is simulated.</summary>
public sealed record EvidenceFixtureResponse(
    Guid EvidenceId,
    Guid PropertyId,
    string FileName,
    string ContentType,
    long SizeBytes,
    string Sha256,
    DateTimeOffset RegisteredAt,
    string Label);

/// <summary>
/// DEVELOPMENT ONLY: registers a demo evidence fixture so the simulated assessment provider can recognise it. This
/// controller does not exist unless the environment is Development and <c>DemoMode:Enabled</c> is true; in any other
/// configuration it is removed from the application model (no route, no OpenAPI entry) and the host refuses to start if
/// the flag is set outside Development.
/// </summary>
/// <remarks>
/// Registering a fixture proves nothing about a document. It records a file and its SHA-256 for the signed-in user and one
/// property, nothing more, and only a fixture registered here is ever treated as clean by the simulated provider.
/// </remarks>
[ApiController]
[Authorize]
[Route("api/development/evidence")]
public sealed class DevelopmentEvidenceController : ControllerBase
{
    /// <summary>Registers a fixture: multipart form with <c>propertyId</c> and a <c>file</c> (at most the configured size).</summary>
    [HttpPost]
    [Consumes("multipart/form-data")]
    [ProducesResponseType<EvidenceFixtureResponse>(StatusCodes.Status201Created)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> Register(
        [FromForm] Guid propertyId,
        IFormFile? file,
        [FromServices] ICurrentUser currentUser,
        [FromServices] IPropertyRepository properties,
        [FromServices] IDateTimeProvider clock,
        [FromServices] EvidenceFixtureStore store,
        CancellationToken cancellationToken)
    {
        if (currentUser.UserId is not { } userId || userId == Guid.Empty)
        {
            return Unauthorized();
        }

        if (propertyId == Guid.Empty || file is null || file.Length == 0 || file.Length > store.MaxBytes)
        {
            return Problem422("A property id and a non-empty file within the size limit are required.");
        }

        if ((await properties.GetExistingIdsAsync([propertyId], cancellationToken)).Count == 0)
        {
            return Problem422("That property does not exist.");
        }

        await using var stream = file.OpenReadStream();
        var fixture = await store.RegisterAsync(userId, propertyId, file.FileName, file.ContentType, stream, clock.UtcNow, cancellationToken);

        return StatusCode(StatusCodes.Status201Created, ToResponse(fixture));
    }

    /// <summary>A fixture the signed-in user registered. Another user's fixture is a 404.</summary>
    [HttpGet("{evidenceId:guid}")]
    [ProducesResponseType<EvidenceFixtureResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Get(
        Guid evidenceId,
        [FromServices] ICurrentUser currentUser,
        [FromServices] EvidenceFixtureStore store,
        CancellationToken cancellationToken)
    {
        var fixture = await store.FindAsync(evidenceId, cancellationToken);

        return fixture is null || currentUser.UserId != fixture.RegisteredByUserId
            ? NotFound()
            : Ok(ToResponse(fixture));
    }

    private static EvidenceFixtureResponse ToResponse(EvidenceFixture fixture) => new(
        fixture.EvidenceId, fixture.PropertyId, fixture.FileName, fixture.ContentType, fixture.SizeBytes, fixture.Sha256,
        fixture.RegisteredAt, EvidenceFixture.Label);

    private IActionResult Problem422(string message)
    {
        var error = new SmartProperty.Common.Results.Error("validation.failed", message, SmartProperty.Common.Results.ErrorType.Validation);
        var response = SmartProperty.Api.Infrastructure.Errors.ApiErrorResponseFactory.FromError(error, HttpContext);

        return StatusCode(response.Status, response);
    }
}
