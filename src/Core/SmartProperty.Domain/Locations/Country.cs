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

    /// <param name="code">The two-letter country code. When omitted it is looked up from the English name, and stays null if unknown.</param>
    public Country(Guid id, int aiId, string nameEn, string nameAr, string? code = null)
    {
        Id = LocationRules.RequireId(id);
        AiId = LocationRules.RequireAiId(aiId);
        NameEn = LocationRules.RequireName(nameEn, nameof(nameEn));
        NameAr = LocationRules.RequireName(nameAr, nameof(nameAr));
        Code = CountryCodes.Normalize(code) ?? CountryCodes.FindByEnglishName(NameEn);
    }

    public Guid Id { get; private set; }
    public int AiId { get; private set; }
    public string NameEn { get; private set; }
    public string NameAr { get; private set; }

    /// <summary>The ISO 3166-1 alpha-2 code, or null when it is not known. A property cannot be placed in a country without one.</summary>
    public string? Code { get; private set; }
}
