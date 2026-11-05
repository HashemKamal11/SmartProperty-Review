using SmartProperty.Application.Abstractions.Messaging;
using SmartProperty.Application.Abstractions.Persistence;
using SmartProperty.Application.Abstractions.Persistence.Repositories;
using SmartProperty.Application.Abstractions.Time;
using SmartProperty.Application.PropertyRegistry.Ai;
using SmartProperty.Common.Results;
using SmartProperty.Domain.PropertyRegistry;

namespace SmartProperty.Application.PropertyRegistry.Create;

/// <summary>
/// Registers a new property and then offers it to the AI search service.
/// </summary>
/// <remarks>
/// The property is committed first. Sending it to the AI service is best effort afterwards, so an outage there
/// never loses the registration: the outcome is recorded on the property and returned as <c>aiSync</c>.
/// </remarks>
public sealed class CreatePropertyCommandHandler(
    IPropertyRepository propertyRepository,
    IAmenityRepository amenityRepository,
    IViewRepository viewRepository,
    ILocationRepository locationRepository,
    IPropertyAiSynchronizer aiSynchronizer,
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
                command, out var propertyType, out var finishingStatus, out var listing) is { } validationError)
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
        var address = new PropertyAddress(
            command.CountryCode!,
            command.City,
            command.Region,
            command.District,
            command.AddressLine,
            command.PostalCode,
            command.Latitude,
            command.Longitude);
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
            listing,
            place.Value.Area,
            place.Value.Compound,
            command.OwnerUserId);

        await propertyRepository.AddAsync(property, cancellationToken);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        await aiSynchronizer.SyncAsync(property, cancellationToken);

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
            PropertyCommandSupport.ToListingDetails(property.Listing),
            PropertyCommandSupport.ToAiSyncDetails(property),
            PropertyCommandSupport.ToPlaceDetails(property)));
    }
}
