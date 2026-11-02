using SmartProperty.Application.Abstractions.Messaging;
using SmartProperty.Application.Abstractions.Persistence.Repositories;
using SmartProperty.Application.Abstractions.Storage;
using SmartProperty.Common.Results;

namespace SmartProperty.Application.PropertyImages.GetFile;

public sealed class GetPropertyImageFileQueryHandler(
    IPropertyImageRepository imageRepository,
    IImageStorage imageStorage)
    : IQueryHandler<GetPropertyImageFileQuery, PropertyImageFile>
{
    public async Task<Result<PropertyImageFile>> Handle(
        GetPropertyImageFileQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        if (query.PropertyId == Guid.Empty || query.ImageId == Guid.Empty)
        {
            return Result<PropertyImageFile>.Failure(PropertyImageErrors.ImageNotFound);
        }

        // The image is looked up under its property, so an id from one property never opens another's file.
        var image = await imageRepository.FindAsync(query.PropertyId, query.ImageId, cancellationToken);

        if (image is null)
        {
            return Result<PropertyImageFile>.Failure(PropertyImageErrors.ImageNotFound);
        }

        var content = await imageStorage.OpenReadAsync(image.StorageKey, cancellationToken);

        if (content is null)
        {
            return Result<PropertyImageFile>.Failure(PropertyImageErrors.ImageNotFound);
        }

        return Result<PropertyImageFile>.Success(
            new PropertyImageFile(content, image.ContentType, image.FileName, image.SizeBytes));
    }
}
