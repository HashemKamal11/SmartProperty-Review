namespace SmartProperty.Domain.Locations;

/// <summary>
/// The place a property is in. This is the level the AI search service requires: a property is located by its area,
/// and optionally by a compound inside it.
/// </summary>
public sealed class Area
{
    private Area()
    {
        NameEn = string.Empty;
        NameAr = string.Empty;
        City = null!;
    }

    public Area(Guid id, int aiId, Guid cityId, string nameEn, string nameAr)
    {
        Id = LocationRules.RequireId(id);
        AiId = LocationRules.RequireAiId(aiId);
        CityId = LocationRules.RequireParentId(cityId, nameof(cityId));
        NameEn = LocationRules.RequireName(nameEn, nameof(nameEn));
        NameAr = LocationRules.RequireName(nameAr, nameof(nameAr));
        City = null!;
    }

    /// <summary>An area already holding its city, as when the tree is built in memory.</summary>
    public Area(Guid id, int aiId, City city, string nameEn, string nameAr)
        : this(id, aiId, LocationRules.NotNull(city).Id, nameEn, nameAr)
    {
        City = city;
    }

    public Guid Id { get; private set; }
    public int AiId { get; private set; }
    public Guid CityId { get; private set; }
    public string NameEn { get; private set; }
    public string NameAr { get; private set; }

    /// <summary>Loaded by the persistence layer when the area is read together with its city.</summary>
    public City City { get; private set; }
}
