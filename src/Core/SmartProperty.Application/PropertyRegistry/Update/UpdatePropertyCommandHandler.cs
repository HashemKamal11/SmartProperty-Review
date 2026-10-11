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
/// No concurrency token guards the row, so two concurrent corrections resolve as last-writer-wins. That is an
/// accepted limitation of this operation and must be revisited when archiving becomes reachable.
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

        var changed = property.UpdateDetails(propertyType, address, ResolveUpdatedAt(property.UpdatedAt));

        if (changed)
        {
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }

        return Result<PropertyDetails>.Success(new PropertyDetails(
            property.Id,
            property.Type,
            property.Status,
            new PropertyAddressDetails(
                property.Address.CountryCode,
                property.Address.City,
                property.Address.Region,
                property.Address.District,
                property.Address.AddressLine,
                property.Address.PostalCode,
                property.Address.Latitude,
                property.Address.Longitude),
            property.CreatedAt,
            property.UpdatedAt));
    }

    /// <summary>
    /// The instant a real change is stamped with: the clock, read once and truncated to the microsecond precision
    /// PostgreSQL stores, so the value returned is exactly the value persisted and later read back. A clock behind
    /// the current update instant keeps that instant rather than moving backwards.
    /// </summary>
    private DateTimeOffset ResolveUpdatedAt(DateTimeOffset currentUpdatedAt)
    {
        var now = dateTimeProvider.UtcNow;
        var truncated = new DateTimeOffset(now.Ticks - (now.Ticks % TimeSpan.TicksPerMicrosecond), now.Offset);

        return truncated >= currentUpdatedAt ? truncated : currentUpdatedAt;
    }
}
