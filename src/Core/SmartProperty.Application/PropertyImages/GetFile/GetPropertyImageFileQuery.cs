using SmartProperty.Application.Abstractions.Messaging;

namespace SmartProperty.Application.PropertyImages.GetFile;

/// <summary>Opens the stored file of one photo.</summary>
public sealed record GetPropertyImageFileQuery(Guid PropertyId, Guid ImageId) : IQuery<PropertyImageFile>;

/// <summary>An opened image file. The caller must dispose <paramref name="Content"/>.</summary>
public sealed record PropertyImageFile(Stream Content, string ContentType, string FileName, long SizeBytes);
