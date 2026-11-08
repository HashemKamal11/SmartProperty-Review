#nullable enable
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using SmartProperty.Api.Contracts;
using SmartProperty.Api.Contracts.OwnerVerification;
using SmartProperty.Api.Infrastructure.Errors;
using SmartProperty.Application.Abstractions.Identity;
using SmartProperty.Application.Abstractions.Messaging;
using SmartProperty.Application.OwnerVerification;
using SmartProperty.Application.OwnerVerification.Get;
using SmartProperty.Application.OwnerVerification.Save;
using SmartProperty.Application.OwnerVerification.Submit;

namespace SmartProperty.Api.Controllers;

/// <summary>
/// The signed-in user's owner profile: the national ID and national address a user must provide, and have approved,
/// before they may add properties.
/// </summary>
/// <remarks>
/// The profile belongs to the user the access token names; no route or body carries a user id, so one user can never
/// read or change another's. No permission is needed, because the data is the user's own. Account data (name, email,
/// phone) is read from the account and not edited here. Once submitted, the profile is locked until a reviewer decides;
/// editing a rejected or approved profile returns it to <c>Draft</c> and it must be submitted again.
/// </remarks>
[ApiController]
[Route("api/owner-profile")]
[Authorize]
public sealed class OwnerProfileController : ControllerBase
{
    private const long MaxProofRequestBytes = 11L * 1024 * 1024;

    /// <summary>The profile with its three steps. A user who has not started gets <c>status: NotStarted</c>.</summary>
    [HttpGet]
    [ProducesResponseType<OwnerProfileResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> Get(
        [FromServices] ICurrentUser currentUser,
        [FromServices] IQueryHandler<GetMyOwnerProfileQuery, OwnerProfileDetails> handler,
        CancellationToken cancellationToken)
    {
        if (currentUser.UserId is not { } userId)
        {
            return Unauthorized(ApiErrorResponseFactory.Unauthorized(HttpContext));
        }

        return ToResult(await handler.Handle(new GetMyOwnerProfileQuery(userId), cancellationToken));
    }

    /// <summary>Saves step 2, the national ID.</summary>
    [HttpPut("identity")]
    [ProducesResponseType<OwnerProfileResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> SaveIdentity(
        SaveOwnerIdentityRequest request,
        [FromServices] ICurrentUser currentUser,
        [FromServices] ICommandHandler<SaveOwnerIdentityCommand, OwnerProfileDetails> handler,
        CancellationToken cancellationToken)
    {
        if (currentUser.UserId is not { } userId)
        {
            return Unauthorized(ApiErrorResponseFactory.Unauthorized(HttpContext));
        }

        return ToResult(await handler.Handle(
            new SaveOwnerIdentityCommand(
                userId,
                request.NationalId,
                request.Nationality,
                request.BirthDate,
                request.IdExpiryDate,
                request.Confirmed),
            cancellationToken));
    }

    /// <summary>Saves step 3, the national address, without its proof image.</summary>
    [HttpPut("address")]
    [ProducesResponseType<OwnerProfileResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> SaveAddress(
        SaveOwnerAddressRequest request,
        [FromServices] ICurrentUser currentUser,
        [FromServices] ICommandHandler<SaveOwnerAddressCommand, OwnerProfileDetails> handler,
        CancellationToken cancellationToken)
    {
        if (currentUser.UserId is not { } userId)
        {
            return Unauthorized(ApiErrorResponseFactory.Unauthorized(HttpContext));
        }

        return ToResult(await handler.Handle(
            new SaveOwnerAddressCommand(
                userId,
                request.BuildingNumber,
                request.StreetName,
                request.District,
                request.City,
                request.PostalCode,
                request.AdditionalNumber,
                request.Confirmed),
            cancellationToken));
    }

    /// <summary>
    /// Uploads the national-address proof image: <c>multipart/form-data</c> with one file in the <c>file</c> field, JPEG,
    /// PNG or WebP, at most 10 MB. Replaces an earlier upload.
    /// </summary>
    [HttpPut("address-proof")]
    [Consumes("multipart/form-data")]
    [RequestSizeLimit(MaxProofRequestBytes)]
    [RequestFormLimits(MultipartBodyLengthLimit = MaxProofRequestBytes)]
    [ProducesResponseType<OwnerProfileResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> UploadAddressProof(
        IFormFile? file,
        [FromServices] ICurrentUser currentUser,
        [FromServices] ICommandHandler<UploadAddressProofCommand, OwnerProfileDetails> handler,
        CancellationToken cancellationToken)
    {
        if (currentUser.UserId is not { } userId)
        {
            return Unauthorized(ApiErrorResponseFactory.Unauthorized(HttpContext));
        }

        if (file is null)
        {
            var missing = ApiErrorResponseFactory.FromError(
                new SmartProperty.Common.Results.Error(
                    "validation.failed",
                    "A file is required in the 'file' field.",
                    SmartProperty.Common.Results.ErrorType.Validation),
                HttpContext);

            return StatusCode(missing.Status, missing);
        }

        await using var stream = file.OpenReadStream();

        return ToResult(await handler.Handle(
            new UploadAddressProofCommand(userId, file.FileName, stream, file.Length),
            cancellationToken));
    }

    /// <summary>The user's own address proof image.</summary>
    [HttpGet("address-proof")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetAddressProof(
        [FromServices] ICurrentUser currentUser,
        [FromServices] IQueryHandler<GetMyAddressProofQuery, AddressProofFile> handler,
        CancellationToken cancellationToken)
    {
        if (currentUser.UserId is not { } userId)
        {
            return Unauthorized(ApiErrorResponseFactory.Unauthorized(HttpContext));
        }

        var result = await handler.Handle(new GetMyAddressProofQuery(userId), cancellationToken);

        return ToFileResult(result);
    }

    /// <summary>
    /// Sends the finished profile to the reviewers: <c>422</c> when a step or the proof image is missing, <c>409</c>
    /// when it is already under review.
    /// </summary>
    [HttpPost("submit")]
    [ProducesResponseType<OwnerProfileResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> Submit(
        [FromServices] ICurrentUser currentUser,
        [FromServices] ICommandHandler<SubmitOwnerProfileCommand, OwnerProfileDetails> handler,
        CancellationToken cancellationToken)
    {
        if (currentUser.UserId is not { } userId)
        {
            return Unauthorized(ApiErrorResponseFactory.Unauthorized(HttpContext));
        }

        return ToResult(await handler.Handle(new SubmitOwnerProfileCommand(userId), cancellationToken));
    }

    private IActionResult ToResult(SmartProperty.Common.Results.Result<OwnerProfileDetails> result)
    {
        if (result.IsFailure)
        {
            var errorResponse = ApiErrorResponseFactory.FromError(result.Error!, HttpContext);
            return StatusCode(errorResponse.Status, errorResponse);
        }

        return Ok(OwnerProfileResponse.From(result.Value));
    }

    private IActionResult ToFileResult(SmartProperty.Common.Results.Result<AddressProofFile> result)
    {
        if (result.IsFailure)
        {
            var errorResponse = ApiErrorResponseFactory.FromError(result.Error!, HttpContext);
            return StatusCode(errorResponse.Status, errorResponse);
        }

        // Identity evidence: never cached, and never sniffed into anything but the image it was checked as.
        Response.Headers.CacheControl = "private, no-store";
        Response.Headers.XContentTypeOptions = "nosniff";

        return File(result.Value.Content, result.Value.ContentType);
    }
}
