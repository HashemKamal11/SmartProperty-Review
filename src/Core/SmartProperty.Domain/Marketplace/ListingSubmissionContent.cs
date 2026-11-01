using SmartProperty.Domain.PropertyRegistry;

namespace SmartProperty.Domain.Marketplace;

/// <summary>Immutable Marketplace content; does not change Registry field requirements.</summary>
public sealed class ListingSubmissionContent
{
    public ListingSubmissionContent(string country, string region, string city, string district,
        string title, string description, PropertyType propertyType, ListingPurpose purpose,
        decimal amount, string currency, RentPeriod? rentPeriod)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(country);
        ArgumentException.ThrowIfNullOrWhiteSpace(region);
        ArgumentException.ThrowIfNullOrWhiteSpace(city);
        ArgumentException.ThrowIfNullOrWhiteSpace(district);
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        ArgumentException.ThrowIfNullOrWhiteSpace(description);
        ArgumentException.ThrowIfNullOrWhiteSpace(currency);
        if (!Enum.IsDefined(propertyType)) throw new ArgumentOutOfRangeException(nameof(propertyType));
        if (!Enum.IsDefined(purpose)) throw new ArgumentOutOfRangeException(nameof(purpose));
        if (purpose == ListingPurpose.Rent && (!rentPeriod.HasValue || !Enum.IsDefined(rentPeriod.Value)))
            throw new ArgumentException("Rent requires Monthly or Yearly.", nameof(rentPeriod));
        if (purpose == ListingPurpose.Sale && rentPeriod.HasValue)
            throw new ArgumentException("Sale must not specify a rental period.", nameof(rentPeriod));

        Country = country;
        Region = region;
        City = city;
        District = district;
        Title = title;
        Description = description;
        PropertyType = propertyType;
        Purpose = purpose;
        Amount = amount;
        Currency = currency;
        RentPeriod = rentPeriod;
    }

    public string Country { get; }
    public string Region { get; }
    public string City { get; }
    public string District { get; }
    public string Title { get; }
    public string Description { get; }
    public PropertyType PropertyType { get; }
    public ListingPurpose Purpose { get; }
    public decimal Amount { get; }
    public string Currency { get; }
    public RentPeriod? RentPeriod { get; }
}
