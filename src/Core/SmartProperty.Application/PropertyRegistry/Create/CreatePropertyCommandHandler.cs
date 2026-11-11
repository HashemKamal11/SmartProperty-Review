using SmartProperty.Application.Abstractions.Messaging;
using SmartProperty.Application.Abstractions.Persistence;
using SmartProperty.Application.Abstractions.Persistence.Repositories;
using SmartProperty.Application.Abstractions.Time;
using SmartProperty.Common.Results;
using SmartProperty.Domain.PropertyRegistry;

namespace SmartProperty.Application.PropertyRegistry.Create;

/// <summary>
/// Registers a new physical property.
/// </summary>
/// <remarks>
/// A property is the physical asset only. Advertising it (sale, rent, prices, promotional text) is a separate
/// Marketplace listing, so creating a property never publishes or offers anything to the AI search service.
/// </remarks>
public sealed class CreatePropertyCommandHandler(
    IPropertyRepository propertyRepository,
    IAmenityRepository amenityRepository,
    IViewRepository viewRepository,
    ILocationRepository locationRepository,
    IDateTimeProvider dateTimeProvider,
    IUnitOfWork unitOfWork)
    : ICommandHandler<CreatePropertyCommand, CreatePropertyResult>
{
    public async Task<Result<CreatePropertyResult>> Handle(
        CreatePropertyCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (CreatePropertyCommandValidator.Validate(
                command, out var propertyType, out var finishingStatus, out var physical) is { } validationError)
        {
            return Result<CreatePropertyResult>.Failure(validationError);
        }

        var amenities = await PropertyCodeResolver.ResolveAsync(
            "Amenity",
            command.AmenityCodes,
            amenityRepository.GetByCodesAsync,
            amenity => amenity.Code,
            CreatePropertyErrors.ValidationFailed,
            cancellationToken);

        if (amenities.IsFailure)
        {
            return Result<CreatePropertyResult>.Failure(amenities.Error!);
        }

        var views = await PropertyCodeResolver.ResolveAsync(
            "View",
            command.ViewCodes,
            viewRepository.GetByCodesAsync,
            view => view.Code,
            CreatePropertyErrors.ValidationFailed,
            cancellationToken);

        if (views.IsFailure)
        {
            return Result<CreatePropertyResult>.Failure(views.Error!);
        }

        var place = await PropertyCommandSupport.ResolvePlaceAsync(
            locationRepository,
            command.AreaId,
            command.CompoundId,
            CreatePropertyErrors.ValidationFailed,
            cancellationToken);

        if (place.IsFailure)
        {
            return Result<CreatePropertyResult>.Failure(place.Error!);
        }

        var createdAt = dateTimeProvider.UtcNow;
        var addressResult = PropertyCommandSupport.BuildAddress(
            place.Value.Area!,
            command.AddressLine,
            command.PostalCode,
            command.Latitude,
            command.Longitude,
            CreatePropertyErrors.ValidationFailed);

        if (addressResult.IsFailure)
        {
            return Result<CreatePropertyResult>.Failure(addressResult.Error!);
        }

        var address = addressResult.Value;
        var property = new Property(
            Guid.NewGuid(),
            propertyType,
            address,
            createdAt,
            command.Bedrooms,
            command.Bathrooms,
            command.BuiltDate,
            finishingStatus,
            amenities.Value,
            views.Value,
            physical,
            place.Value.Area,
            place.Value.Compound,
            command.OwnerUserId);

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
            property.UpdatedAt,
            property.Bedrooms,
            property.Bathrooms,
            property.BuiltDate,
            property.FinishingStatus,
            PropertyCommandSupport.ToAmenityDetails(property.Amenities),
            PropertyCommandSupport.ToViewDetails(property.Views),
            PropertyCommandSupport.ToPhysicalDetails(property.Physical),
            PropertyCommandSupport.ToPlaceDetails(property)));
    }
}
