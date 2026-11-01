using SmartProperty.Application.Abstractions.Messaging;

namespace SmartProperty.Application.PropertyRegistry.Amenities;

/// <summary>Every amenity a property can offer, in display order.</summary>
public sealed record GetAmenitiesQuery : IQuery<IReadOnlyList<AmenityDetails>>;
