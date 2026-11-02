namespace SmartProperty.Domain.PropertyRegistry;

/// <summary>
/// What kind of property this is. The set mirrors the closed list of the AI search service (<c>/v1/options</c>),
/// so every property can be sent to it unchanged.
/// </summary>
/// <remarks>
/// The numeric values are stored by the marketplace module inside persisted listing snapshots, so 1, 2, and 3
/// must never be reassigned. Value 3 was <c>Unit</c> before the AI vocabulary was adopted and is now
/// <see cref="Apartment"/>, the closest match.
/// </remarks>
public enum PropertyType
{
    Land = 1,
    Building = 2,
    Apartment = 3,
    Villa = 4,
    Duplex = 5,
    Studio = 6,
    Chalet = 7,
    Townhouse = 8,
    TwinHouse = 9,
    Penthouse = 10,
    StandaloneVilla = 11,
    HotelApartment = 12,
    Office = 13,
    Shop = 14,
    Warehouse = 15,
    Farm = 16
}
