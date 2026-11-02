namespace SmartProperty.Domain.Locations;

/// <summary>
/// A country. Place data is owned by the AI search service and copied here the first time it is needed, so every
/// place keeps that service's own id (<see cref="AiId"/>), which is what is sent when a property is offered to it.
/// </summary>
public sealed class Country
{
    private Country()
    {
        NameEn = string.Empty;
        NameAr = string.Empty;
    }

    public Country(Guid id, int aiId, string nameEn, string nameAr)
    {
        Id = LocationRules.RequireId(id);
        AiId = LocationRules.RequireAiId(aiId);
        NameEn = LocationRules.RequireName(nameEn, nameof(nameEn));
        NameAr = LocationRules.RequireName(nameAr, nameof(nameAr));
    }

    public Guid Id { get; private set; }
    public int AiId { get; private set; }
    public string NameEn { get; private set; }
    public string NameAr { get; private set; }
}
