using SmartProperty.Application.Abstractions.Messaging;
using SmartProperty.Application.Abstractions.Persistence;
using SmartProperty.Application.Abstractions.Persistence.Repositories;
using SmartProperty.Application.Abstractions.Time;
using SmartProperty.Common.Results;
using SmartProperty.Domain.PropertyRegistry;

namespace SmartProperty.Application.PropertyRegistry.Archive;

/// <summary>
/// Archives one active canonical property: its status becomes Archived and its update instant is stamped.
/// </summary>
/// <remarks>
/// The only removal the registry supports. The row is kept, because other modules reference it as the canonical
/// asset, and nothing but the status and the update instant changes.
///
/// Not idempotent: archiving an archived property is refused as <c>properties.archived</c> rather than reported
/// as a success, so a caller can tell its own archive from someone else's.
///
/// Status is the row's concurrency token. Of two concurrent archives only the first to commit succeeds, and the
/// loser is refused as <c>properties.archived</c>. An update never changes the status, so it cannot make an archive
/// fail; it is the update that loses when an archive commits first.
/// </remarks>
public sealed class ArchivePropertyCommandHandler(
    IPropertyRepository propertyRepository,
    IDateTimeProvider dateTimeProvider,
    IUnitOfWork unitOfWork)
    : ICommandHandler<ArchivePropertyCommand, PropertyDetails>
{
    public async Task<Result<PropertyDetails>> Handle(
        ArchivePropertyCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        // No property can have the empty id, so asking the database would only confirm what is already known.
        if (command.Id == Guid.Empty)
        {
            return Result<PropertyDetails>.Failure(ArchivePropertyErrors.NotFound);
        }

        var property = await propertyRepository.GetByIdAsync(command.Id, cancellationToken);

        if (property is null)
        {
            return Result<PropertyDetails>.Failure(ArchivePropertyErrors.NotFound);
        }

        // Asked here so an archived property returns a stable business error. The domain enforces the same rule
        // and would throw; this is the translation, not a second state machine.
        if (property.Status != PropertyStatus.Active)
        {
            return Result<PropertyDetails>.Failure(ArchivePropertyErrors.Archived);
        }

        property.Archive(PropertyCommandSupport.ResolveUpdatedAt(dateTimeProvider, property.UpdatedAt));

        try
        {
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }
        catch (ConcurrencyConflictException exception)
            when (exception.Resource == PersistenceResource.Property)
        {
            // The status changed after the load, and Archived is the only status an Active property moves to.
            return Result<PropertyDetails>.Failure(ArchivePropertyErrors.Archived);
        }

        return Result<PropertyDetails>.Success(PropertyCommandSupport.ToDetails(property));
    }
}
