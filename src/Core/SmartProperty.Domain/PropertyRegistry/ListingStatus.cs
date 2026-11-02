namespace SmartProperty.Domain.PropertyRegistry;

/// <summary>
/// Where the property stands in the market, as the AI search service tracks it. Separate from
/// <see cref="PropertyStatus"/>, which is the registry lifecycle (active or archived).
/// </summary>
public enum ListingStatus
{
    /// <summary>متاح</summary>
    Available = 1,

    /// <summary>محجوز</summary>
    Reserved = 2,

    /// <summary>مباع</summary>
    Sold = 3,

    /// <summary>مؤجر</summary>
    Rented = 4
}
