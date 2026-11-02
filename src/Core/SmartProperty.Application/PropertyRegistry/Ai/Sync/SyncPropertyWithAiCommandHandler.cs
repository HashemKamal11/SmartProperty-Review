using SmartProperty.Application.Abstractions.Messaging;
using SmartProperty.Application.Abstractions.Persistence.Repositories;
using SmartProperty.Application.PropertyRegistry.GetById;
using SmartProperty.Common.Results;
using SmartProperty.Domain.PropertyRegistry;

namespace SmartProperty.Application.PropertyRegistry.Ai.Sync;

public sealed class SyncPropertyWithAiCommandHandler(
    IPropertyRepository propertyRepository,
    IPropertyAiSynchronizer synchronizer)
    : ICommandHandler<SyncPropertyWithAiCommand, PropertyDetails>
{
    public static readonly Error Archived = new(
        "properties.archived",
        "An archived property is not held by AI search.",
        ErrorType.Conflict);

    public async Task<Result<PropertyDetails>> Handle(
        SyncPropertyWithAiCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        var property = command.Id == Guid.Empty
            ? null
            : await propertyRepository.GetByIdAsync(command.Id, cancellationToken);

        if (property is null)
        {
            return Result<PropertyDetails>.Failure(GetPropertyByIdErrors.NotFound);
        }

        if (property.Status != PropertyStatus.Active)
        {
            return Result<PropertyDetails>.Failure(Archived);
        }

        await synchronizer.SyncAsync(property, cancellationToken);

        return Result<PropertyDetails>.Success(PropertyCommandSupport.ToDetails(property));
    }
}
