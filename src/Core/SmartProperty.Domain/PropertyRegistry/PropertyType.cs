namespace SmartProperty.Domain.PropertyRegistry;

/// <summary>
/// Physical form of a property. Usage classification such as residential, commercial, or industrial is a
/// separate future axis and must not be expressed here.
/// </summary>
public enum PropertyType
{
    Land = 1,
    Building = 2,
    Unit = 3
}
