#nullable enable
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SmartProperty.Api.Contracts;
using SmartProperty.Api.Contracts.PropertyImages;
using SmartProperty.Api.Infrastructure.Authorization;
using SmartProperty.Api.Infrastructure.Errors;
using SmartProperty.Application.Abstractions.Identity;
using SmartProperty.Application.Abstractions.Messaging;
using SmartProperty.Application.Authorization;
using SmartProperty.Application.PropertyImages;
using SmartProperty.Application.PropertyImages.Delete;
using SmartProperty.Application.PropertyImages.GetFile;
using SmartProperty.Application.PropertyImages.Reorder;
using SmartProperty.Application.PropertyImages.Upload;

namespace SmartProperty.Api.Controllers;

/// <summary>
/// The photos of a property: an ordered group, the first of which is its cover.
/// </summary>
/// <remarks>
/// Changing the group is allowed to administrators holding <c>property.update</c> and to the property's own owner while
/// they are an approved owner (see <see cref="PropertyImageAccess"/>); anyone else gets <c>403</c>. The photos of a
/// property are listed in the property itself (<c>images</c>), so there is no separate list endpoint. The file endpoint
/// is public, because a browser cannot send an access token from an <c>img</c> tag; the ids are random, so a photo's
/// address cannot be guessed.
/// </remarks>
[ApiController]
[Route("api/properties/{propertyId:guid}/images")]
public sealed class PropertyImagesController : ControllerBase
{
    /// <summary>The most one upload request may carry: the file limit times the per-file limit, with room for the form.</summary>
    private const long MaxRequestBytes = 60L * 1024 * 1024;

    internal static string UrlOf(Guid propertyId, Guid imageId)
    {
        return $"/api/properties/{propertyId}/images/{imageId}/file";
    }

    internal static PropertyImageResponse ToResponse(Guid propertyId, PropertyImageDetails image)
    {
        return new PropertyImageResponse(
            image.Id,
            UrlOf(propertyId, image.Id),
            image.FileName,
            image.ContentType,
            image.SizeBytes,
            image.SortOrder,
            image.SortOrder == 0);
    }

    /// <summary>
    /// Adds photos to the end of the group. Send <c>multipart/form-data</c> with one or more files in the
    /// <c>files</c> field: JPEG, PNG, or WebP, at most 5 MB each, at most 10 per request and 20 per property. All the
    /// files are added or none is.
    /// </summary>
    /// <remarks>
    /// The type is read from the file's own bytes, not from its name or declared type. An archived property is refused
    /// with <c>409 properties.archived</c>.
    /// </remarks>
    [HttpPost]
    [Authorize]
    [Consumes("multipart/form-data")]
    [RequestSizeLimit(MaxRequestBytes)]
    [RequestFormLimits(MultipartBodyLengthLimit = MaxRequestBytes)]
    [ProducesResponseType<PropertyImageResponse[]>(StatusCodes.Status201Created)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> Upload(
        Guid propertyId,
        IFormFileCollection files,
        [FromServices] ICurrentUser currentUser,
        [FromServices] ICommandHandler<UploadPropertyImagesCommand, IReadOnlyList<PropertyImageDetails>> handler,
        CancellationToken cancellationToken)
    {
        if (currentUser.UserId is not { } userId)
        {
            return Unauthorized(ApiErrorResponseFactory.Unauthorized(HttpContext));
        }

        var streams = new List<Stream>(files.Count);

        try
        {
            var uploads = new List<ImageUpload>(files.Count);

            foreach (var file in files)
            {
                var stream = file.OpenReadStream();
                streams.Add(stream);
                uploads.Add(new ImageUpload(file.FileName, stream, file.Length));
            }

            var result = await handler.Handle(new UploadPropertyImagesCommand(userId, propertyId, uploads), cancellationToken);

            if (result.IsFailure)
            {
                var errorResponse = ApiErrorResponseFactory.FromError(result.Error!, HttpContext);
                return StatusCode(errorResponse.Status, errorResponse);
            }

            return StatusCode(
                StatusCodes.Status201Created,
                result.Value.Select(image => ToResponse(propertyId, image)).ToArray());
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
    /// Sets the order of the photos: send every image id, exactly once; the first becomes the cover.
    /// </summary>
    [HttpPut("order")]
    [Authorize]
    [ProducesResponseType<PropertyImageResponse[]>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> Reorder(
        Guid propertyId,
        ReorderPropertyImagesRequest request,
        [FromServices] ICurrentUser currentUser,
        [FromServices] ICommandHandler<ReorderPropertyImagesCommand, IReadOnlyList<PropertyImageDetails>> handler,
        CancellationToken cancellationToken)
    {
        if (currentUser.UserId is not { } userId)
        {
            return Unauthorized(ApiErrorResponseFactory.Unauthorized(HttpContext));
        }

        var result = await handler.Handle(
            new ReorderPropertyImagesCommand(userId, propertyId, request.ImageIds),
            cancellationToken);

        if (result.IsFailure)
        {
            var errorResponse = ApiErrorResponseFactory.FromError(result.Error!, HttpContext);
            return StatusCode(errorResponse.Status, errorResponse);
        }

        return Ok(result.Value.Select(image => ToResponse(propertyId, image)).ToArray());
    }

    /// <summary>Removes one photo. <c>204</c> whether or not the property had it.</summary>
    [HttpDelete("{imageId:guid}")]
    [Authorize]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Delete(
        Guid propertyId,
        Guid imageId,
        [FromServices] ICurrentUser currentUser,
        [FromServices] ICommandHandler<DeletePropertyImageCommand, DeletePropertyImageResult> handler,
        CancellationToken cancellationToken)
    {
        if (currentUser.UserId is not { } userId)
        {
            return Unauthorized(ApiErrorResponseFactory.Unauthorized(HttpContext));
        }

        var result = await handler.Handle(new DeletePropertyImageCommand(userId, propertyId, imageId), cancellationToken);

        if (result.IsFailure)
        {
            var errorResponse = ApiErrorResponseFactory.FromError(result.Error!, HttpContext);
            return StatusCode(errorResponse.Status, errorResponse);
        }

        return NoContent();
    }

    /// <summary>
    /// The photo itself. Public and cacheable for a year: a photo is never edited in place, a changed photo is a new id.
    /// </summary>
    [HttpGet("{imageId:guid}/file")]
    [AllowAnonymous]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status304NotModified)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetFile(
        Guid propertyId,
        Guid imageId,
        [FromServices] IQueryHandler<GetPropertyImageFileQuery, PropertyImageFile> handler,
        CancellationToken cancellationToken)
    {
        var etag = $"\"{imageId:n}\"";

        // The photo behind an id never changes, so a client that already has it needs no body.
        if (Request.Headers.IfNoneMatch.Contains(etag))
        {
            Response.Headers.ETag = etag;
            return StatusCode(StatusCodes.Status304NotModified);
        }

        var result = await handler.Handle(new GetPropertyImageFileQuery(propertyId, imageId), cancellationToken);

        if (result.IsFailure)
        {
            var errorResponse = ApiErrorResponseFactory.FromError(result.Error!, HttpContext);
            return StatusCode(errorResponse.Status, errorResponse);
        }

        Response.Headers.ETag = etag;
        Response.Headers.CacheControl = "public, max-age=31536000, immutable";

        // Uploaded content is served back, so the browser must not guess it is anything but the image it was checked as.
        Response.Headers.XContentTypeOptions = "nosniff";

        return File(result.Value.Content, result.Value.ContentType, enableRangeProcessing: true);
    }
}
