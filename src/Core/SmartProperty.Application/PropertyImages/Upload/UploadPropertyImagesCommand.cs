using SmartProperty.Application.Abstractions.Messaging;

namespace SmartProperty.Application.PropertyImages.Upload;

/// <summary>
/// Adds photos to the end of a property's group. All of them are added or none: one bad file refuses the whole
/// upload.
/// </summary>
public sealed record UploadPropertyImagesCommand(Guid PropertyId, IReadOnlyList<ImageUpload> Files)
    : ICommand<IReadOnlyList<PropertyImageDetails>>;

/// <summary>One file being uploaded. The stream must be readable from its start and support seeking.</summary>
/// <param name="FileName">The name the client gave it; used for display only.</param>
/// <param name="Content">The file's bytes. The caller disposes it.</param>
/// <param name="Length">The file's size in bytes.</param>
public sealed record ImageUpload(string? FileName, Stream Content, long Length);
