using SmartProperty.Application.Abstractions.Messaging;
using SmartProperty.Application.Abstractions.Persistence;
using SmartProperty.Application.Abstractions.Persistence.Repositories;
using SmartProperty.Application.Abstractions.Time;
using SmartProperty.Common.Results;
using SmartProperty.Domain.PropertyRegistry;

namespace SmartProperty.Application.PropertyRegistry.Update;

/// <summary>
/// Corrects the current details of one canonical property: its physical form and its whole address.
/// </summary>
/// <remarks>
/// A current-state correction, not a revision: the row is overwritten and no history is kept. Identity, lifecycle,
/// and the creation instant are never touched.
///
/// Every precondition is checked before the aggregate is mutated, so a refused update leaves it exactly as it was.
/// An update whose normalized values are already current changes nothing and commits nothing.
///
/// Status is the row's concurrency token. Two concurrent corrections still resolve as last-writer-wins, because
/// neither changes the status; but an update that loaded the property Active and saves after an archive committed
/// no longer succeeds against the archived row, and is refused as <c>properties.archived</c>.
/// </remarks>
public sealed class UpdatePropertyCommandHandler(
    IPropertyRepository propertyRepository,
    IDateTimeProvider dateTimeProvider,
    IUnitOfWork unitOfWork)
    : ICommandHandler<UpdatePropertyCommand, PropertyDetails>
{
    public async Task<Result<PropertyDetails>> Handle(
        UpdatePropertyCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (UpdatePropertyCommandValidator.Validate(command, out var propertyType) is { } validationError)
        {
            return Result<PropertyDetails>.Failure(validationError);
        }

        // No property can have the empty id, so asking the database would only confirm what is already known.
        if (command.Id == Guid.Empty)
        {
            return Result<PropertyDetails>.Failure(UpdatePropertyErrors.NotFound);
        }

        var property = await propertyRepository.GetByIdAsync(command.Id, cancellationToken);

        if (property is null)
        {
            return Result<PropertyDetails>.Failure(UpdatePropertyErrors.NotFound);
        }

        // Asked here so an archived property returns a stable business error. The domain enforces the same rule
        // and would throw; this is the translation, not a second state machine.
        if (property.Status != PropertyStatus.Active)
        {
            return Result<PropertyDetails>.Failure(UpdatePropertyErrors.Archived);
        }

        var address = new PropertyAddress(
            command.CountryCode!,
            command.City,
            command.Region,
            command.District,
            command.AddressLine,
            command.PostalCode,
            command.Latitude,
            command.Longitude);

        var changed = property.UpdateDetails(
            propertyType,
            address,
            PropertyCommandSupport.ResolveUpdatedAt(dateTimeProvider, property.UpdatedAt));

        if (changed)
        {
            try
            {
                await unitOfWork.SaveChangesAsync(cancellationToken);
            }
            catch (ConcurrencyConflictException exception)
                when (exception.Resource == PersistenceResource.Property)
            {
                // The status changed after the load, and Active is the only status an update starts from.
                return Result<PropertyDetails>.Failure(UpdatePropertyErrors.Archived);
            }
        }

        return Result<PropertyDetails>.Success(PropertyCommandSupport.ToDetails(property));
    }
}
