using SmartProperty.Application.Abstractions.Messaging;

namespace SmartProperty.Application.PropertyImages.Delete;

/// <summary>
/// Removes one photo from a property. Idempotent: an image that is not there is already removed.
/// </summary>
public sealed record DeletePropertyImageCommand(Guid PropertyId, Guid ImageId) : ICommand<DeletePropertyImageResult>;

/// <param name="Removed">True when this call removed an image; false when there was none.</param>
public sealed record DeletePropertyImageResult(bool Removed);
