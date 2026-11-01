using SmartProperty.Domain.PropertyRegistry;

namespace SmartProperty.Application.Abstractions.Persistence.Repositories;

/// <summary>
/// The canonical registry facts about a property that a listing write needs, read while the property row is locked.
/// </summary>
/// <remarks>
/// Location and property type for a listing come from here, never from a request. The registry allows a missing
/// region, city or district, while a listing requires all of them, so those are nullable on purpose: a caller that
/// finds one missing refuses to build a listing rather than inventing a value.
/// </remarks>
public sealed record LockedPropertyView(
    Guid PropertyId,
    bool IsActive,
    bool HasMarketplaceReservation,
    PropertyType PropertyType,
    string CountryCode,
    string? Region,
    string? City,
    string? District);
