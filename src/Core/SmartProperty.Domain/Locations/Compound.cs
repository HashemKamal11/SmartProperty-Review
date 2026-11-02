namespace SmartProperty.Domain.Locations;

public sealed class Compound
{
    private Compound()
    {
        NameEn = string.Empty;
        NameAr = string.Empty;
        Area = null!;
    }

    public Compound(Guid id, int aiId, Guid areaId, string nameEn, string nameAr)
    {
        Id = LocationRules.RequireId(id);
        AiId = LocationRules.RequireAiId(aiId);
        AreaId = LocationRules.RequireParentId(areaId, nameof(areaId));
        NameEn = LocationRules.RequireName(nameEn, nameof(nameEn));
        NameAr = LocationRules.RequireName(nameAr, nameof(nameAr));
        Area = null!;
    }

    /// <summary>A compound already holding its area, as when the tree is built in memory.</summary>
    public Compound(Guid id, int aiId, Area area, string nameEn, string nameAr)
        : this(id, aiId, LocationRules.NotNull(area).Id, nameEn, nameAr)
    {
        Area = area;
    }

    public Guid Id { get; private set; }
    public int AiId { get; private set; }
    public Guid AreaId { get; private set; }
    public string NameEn { get; private set; }
    public string NameAr { get; private set; }

    /// <summary>Loaded by the persistence layer when the compound is read together with its area.</summary>
    public Area Area { get; private set; }
}
