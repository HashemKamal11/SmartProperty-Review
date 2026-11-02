namespace SmartProperty.Domain.Locations;

public sealed class City
{
    private City()
    {
        NameEn = string.Empty;
        NameAr = string.Empty;
        Country = null!;
    }

    public City(Guid id, int aiId, Guid countryId, string nameEn, string nameAr)
    {
        Id = LocationRules.RequireId(id);
        AiId = LocationRules.RequireAiId(aiId);
        CountryId = LocationRules.RequireParentId(countryId, nameof(countryId));
        NameEn = LocationRules.RequireName(nameEn, nameof(nameEn));
        NameAr = LocationRules.RequireName(nameAr, nameof(nameAr));
        Country = null!;
    }

    /// <summary>A city already holding its country, as when the tree is built in memory.</summary>
    public City(Guid id, int aiId, Country country, string nameEn, string nameAr)
        : this(id, aiId, LocationRules.NotNull(country).Id, nameEn, nameAr)
    {
        Country = country;
    }

    public Guid Id { get; private set; }
    public int AiId { get; private set; }
    public Guid CountryId { get; private set; }
    public string NameEn { get; private set; }
    public string NameAr { get; private set; }

    /// <summary>Loaded by the persistence layer when the city is read together with its country.</summary>
    public Country Country { get; private set; }
}
