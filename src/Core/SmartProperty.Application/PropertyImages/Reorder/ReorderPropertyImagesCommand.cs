using SmartProperty.Application.Abstractions.Messaging;

namespace SmartProperty.Application.PropertyImages.Reorder;

/// <summary>
/// Sets the order of a property's photos: the first id becomes the cover. The list must name every image the property
/// has, exactly once; to change which photos exist, upload or delete.
/// </summary>
public sealed record ReorderPropertyImagesCommand(Guid PropertyId, IReadOnlyList<Guid>? ImageIds)
    : ICommand<IReadOnlyList<PropertyImageDetails>>;
