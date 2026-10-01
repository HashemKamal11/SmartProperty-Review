namespace SmartProperty.Domain.PropertyRegistry;

/// <summary>
/// Registry lifecycle of a property. Archiving replaces destructive deletion; verification states belong to
/// future workflow and document functionality.
/// </summary>
public enum PropertyStatus
{
    Active = 1,
    Archived = 2
}
