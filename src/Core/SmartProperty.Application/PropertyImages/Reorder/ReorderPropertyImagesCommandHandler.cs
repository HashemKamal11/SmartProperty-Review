using SmartProperty.Application.Abstractions.Messaging;
using SmartProperty.Application.Abstractions.Persistence;
using SmartProperty.Application.Abstractions.Persistence.Repositories;
using SmartProperty.Common.Results;
using SmartProperty.Domain.PropertyRegistry;

namespace SmartProperty.Application.PropertyImages.Reorder;

public sealed class ReorderPropertyImagesCommandHandler(
    IPropertyRepository propertyRepository,
    IPropertyImageRepository imageRepository,
    IUnitOfWork unitOfWork)
    : ICommandHandler<ReorderPropertyImagesCommand, IReadOnlyList<PropertyImageDetails>>
{
    public async Task<Result<IReadOnlyList<PropertyImageDetails>>> Handle(
        ReorderPropertyImagesCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (command.ImageIds is null)
        {
            return Failure(PropertyImageErrors.ValidationFailed("The list of image ids is required."));
        }

        if (command.PropertyId == Guid.Empty)
        {
            return Failure(PropertyImageErrors.PropertyNotFound);
        }

        var property = await propertyRepository.GetDetailsByIdAsync(command.PropertyId, cancellationToken);

        if (property is null)
        {
            return Failure(PropertyImageErrors.PropertyNotFound);
        }

        if (property.Status != PropertyStatus.Active)
        {
            return Failure(PropertyImageErrors.PropertyArchived);
        }

        var images = await imageRepository.ListAsync(command.PropertyId, cancellationToken);

        if (command.ImageIds.Distinct().Count() != command.ImageIds.Count)
        {
            return Failure(PropertyImageErrors.ValidationFailed("An image id appears more than once."));
        }

        var current = images.Select(image => image.Id).ToHashSet();
        if (!current.SetEquals(command.ImageIds))
        {
            return Failure(PropertyImageErrors.ValidationFailed(
                "The list must name every image of the property exactly once."));
        }

        var byId = images.ToDictionary(image => image.Id);
        var changed = false;

        for (var position = 0; position < command.ImageIds.Count; position++)
        {
            var image = byId[command.ImageIds[position]];

            if (image.SortOrder != position)
            {
                image.MoveTo(position);
                changed = true;
            }
        }

        if (changed)
        {
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }

        return Result<IReadOnlyList<PropertyImageDetails>>.Success(
            command.ImageIds.Select(id => byId[id])
                .Select(image => new PropertyImageDetails(
                    image.Id, image.FileName, image.ContentType, image.SizeBytes, image.SortOrder))
                .ToList());
    }

    private static Result<IReadOnlyList<PropertyImageDetails>> Failure(Error error)
    {
        return Result<IReadOnlyList<PropertyImageDetails>>.Failure(error);
    }
}
