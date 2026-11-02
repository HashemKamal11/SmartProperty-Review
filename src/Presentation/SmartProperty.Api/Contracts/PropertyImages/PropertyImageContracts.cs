#nullable enable
namespace SmartProperty.Api.Contracts.PropertyImages;

/// <summary>
/// One photo of a property. <c>url</c> is a path on this API that serves the file; use it as an <c>img</c> source. The
/// first photo of a property (<c>sortOrder</c> 0) is its cover.
/// </summary>
public sealed record PropertyImageResponse(
    Guid Id,
    string Url,
    string FileName,
    string ContentType,
    long SizeBytes,
    int SortOrder,
    bool IsCover);

/// <summary>The new order of a property's photos: every image id, exactly once. The first becomes the cover.</summary>
public sealed record ReorderPropertyImagesRequest(IReadOnlyList<Guid>? ImageIds);
