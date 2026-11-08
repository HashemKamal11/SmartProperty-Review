namespace SmartProperty.Application.PropertyRegistry.List;

/// <summary>
/// How a registry page is ordered. Every order ends with the newest-first, id-descending tiebreaker so paging never
/// repeats or skips a property.
/// </summary>
public enum PropertyListSortOrder
{
    /// <summary>
    /// Most relevant first. The default. Ranks by the signed-in user's latest stored match percentage (highest
    /// first, properties without a match last), then by how well the text search matches (title, then location,
    /// then description). With neither signal it is the same as <see cref="Newest"/>.
    /// </summary>
    Relevance = 1,

    /// <summary>Most recently created first.</summary>
    Newest = 2,

    /// <summary>Lowest listing price first; properties without a price come last.</summary>
    PriceAsc = 3,

    /// <summary>Highest listing price first; properties without a price come last.</summary>
    PriceDesc = 4
}
