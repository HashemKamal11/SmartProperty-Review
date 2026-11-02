namespace SmartProperty.Domain.Locations;

/// <summary>
/// The levels of the place tree, from the widest to the narrowest. Each level but the first hangs under one
/// parent of the level above.
/// </summary>
public enum LocationLevel
{
    Country = 1,
    City = 2,
    Area = 3,
    Compound = 4
}
