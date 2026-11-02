using SmartProperty.Application.Abstractions.Messaging;
using SmartProperty.Application.Abstractions.Persistence;
using SmartProperty.Application.Abstractions.Persistence.Repositories;
using SmartProperty.Application.Abstractions.Storage;
using SmartProperty.Common.Results;
using SmartProperty.Domain.PropertyRegistry;

namespace SmartProperty.Application.PropertyImages.Delete;

/// <remarks>
/// The row is removed first and the file after, so a failure between the two leaves an unreferenced file (harmless)
/// rather than a row pointing at a file that is gone (a broken image on the property).
/// </remarks>
public sealed class DeletePropertyImageCommandHandler(
    IPropertyRepository propertyRepository,
    IPropertyImageRepository imageRepository,
    IImageStorage imageStorage,
    IUnitOfWork unitOfWork)
    : ICommandHandler<DeletePropertyImageCommand, DeletePropertyImageResult>
{
    public async Task<Result<DeletePropertyImageResult>> Handle(
        DeletePropertyImageCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (command.PropertyId == Guid.Empty)
        {
            return Result<DeletePropertyImageResult>.Failure(PropertyImageErrors.PropertyNotFound);
        }

        var property = await propertyRepository.GetDetailsByIdAsync(command.PropertyId, cancellationToken);

        if (property is null)
        {
            return Result<DeletePropertyImageResult>.Failure(PropertyImageErrors.PropertyNotFound);
        }

        if (property.Status != PropertyStatus.Active)
        {
            return Result<DeletePropertyImageResult>.Failure(PropertyImageErrors.PropertyArchived);
        }

        var key = await imageRepository.RemoveAsync(command.PropertyId, command.ImageId, cancellationToken);

        if (key is null)
        {
            return Result<DeletePropertyImageResult>.Success(new DeletePropertyImageResult(false));
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);

        try
        {
            await imageStorage.DeleteAsync(key, cancellationToken);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // The image is already gone from the property; a file that could not be deleted is only wasted space.
        }

        return Result<DeletePropertyImageResult>.Success(new DeletePropertyImageResult(true));
    }
}
