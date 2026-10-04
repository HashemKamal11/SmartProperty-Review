using SmartProperty.Application.Abstractions.Messaging;
using SmartProperty.Application.Abstractions.Persistence;
using SmartProperty.Application.Abstractions.Persistence.Repositories;
using SmartProperty.Application.Abstractions.Time;
using SmartProperty.Common.Results;
using SmartProperty.Domain.PropertyRegistry;

namespace SmartProperty.Application.PropertyRegistry.Create;

public sealed class CreatePropertyCommandHandler(
    IPropertyRepository propertyRepository,
    IDateTimeProvider dateTimeProvider,
    IUnitOfWork unitOfWork)
    : ICommandHandler<CreatePropertyCommand, CreatePropertyResult>
{
    public async Task<Result<CreatePropertyResult>> Handle(
        CreatePropertyCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (CreatePropertyCommandValidator.Validate(command, out var propertyType) is { } validationError)
        {
            return Result<CreatePropertyResult>.Failure(validationError);
        }

        var createdAt = dateTimeProvider.UtcNow;
        var address = new PropertyAddress(
            command.CountryCode!,
            command.City,
            command.Region,
            command.District,
            command.AddressLine,
            command.PostalCode,
            command.Latitude,
            command.Longitude);
        var property = new Property(Guid.NewGuid(), propertyType, address, createdAt);

        await propertyRepository.AddAsync(property, cancellationToken);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return Result<CreatePropertyResult>.Success(new CreatePropertyResult(
            property.Id,
            property.Type,
            property.Status,
            new CreatePropertyAddressResult(
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
}
