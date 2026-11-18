using SmartProperty.Domain.PropertyRegistry;

namespace SmartProperty.Domain.Marketplace;

/// <summary>Immutable Marketplace content; does not change Registry field requirements.</summary>
/// <remarks>
/// A Sale carries one price. A Rent may carry several <see cref="PricingOptions"/> (Daily, Weekly, Monthly and Yearly,
/// each with its own amount and currency) and its <see cref="RentalTerms"/>. For such a rental the plain
/// <see cref="Amount"/>, <see cref="Currency"/> and <see cref="RentPeriod"/> are the primary option, so every reader
/// that knows only one price keeps working. A rental stored before options existed has the single price and no
/// options or terms.
/// </remarks>
public sealed class ListingSubmissionContent
{
    public ListingSubmissionContent(string country, string region, string city, string district,
        string title, string description, PropertyType propertyType, ListingPurpose purpose,
        decimal amount, string currency, RentPeriod? rentPeriod,
        IEnumerable<RentPricingOption>? pricingOptions = null, RentalTerms? rentalTerms = null, bool? negotiable = null,
        DateOnly? availableFrom = null)
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
            throw new ArgumentException("Rent requires Daily, Weekly, Monthly or Yearly.", nameof(rentPeriod));
        if (purpose == ListingPurpose.Sale && rentPeriod.HasValue)
            throw new ArgumentException("Sale must not specify a rental period.", nameof(rentPeriod));

        if (purpose == ListingPurpose.Rent && negotiable.HasValue)
            throw new ArgumentException("Only a sale states whether its price is negotiable.", nameof(negotiable));
        if (purpose == ListingPurpose.Rent && availableFrom.HasValue)
            throw new ArgumentException("A rental states its availability as windows, not one date.", nameof(availableFrom));

        var options = pricingOptions?.ToArray() ?? [];
        if (purpose == ListingPurpose.Sale && (options.Length > 0 || rentalTerms is not null))
            throw new ArgumentException("Sale must not specify rental pricing options or terms.", nameof(pricingOptions));
        if (options.Any(option => option is null))
            throw new ArgumentException("Pricing options cannot be null.", nameof(pricingOptions));
        if (options.Select(option => option.Period).Distinct().Count() != options.Length)
            throw new ArgumentException("A rental offers each pricing period at most once.", nameof(pricingOptions));
        if ((options.Length > 0) != (rentalTerms is not null))
            throw new ArgumentException("Pricing options and rental terms are given together.", nameof(rentalTerms));
        if (options.Length > 0)
        {
            // The plain price is the primary option, so nothing can contradict the options it summarizes.
            var primary = RentPricingOption.PrimaryOf(options);
            if (primary.Period != rentPeriod || primary.Amount != amount || !string.Equals(primary.Currency, currency, StringComparison.Ordinal))
                throw new ArgumentException("The plain price must be the primary pricing option.", nameof(amount));
        }

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
        PricingOptions = Array.AsReadOnly(options);
        RentalTerms = rentalTerms;
        Negotiable = negotiable;
        AvailableFrom = availableFrom;
    }

    /// <summary>Builds a rental from its pricing options, deriving the plain price from the primary option.</summary>
    public static ListingSubmissionContent ForRent(string country, string region, string city, string district,
        string title, string description, PropertyType propertyType,
        IEnumerable<RentPricingOption> pricingOptions, RentalTerms rentalTerms)
    {
        ArgumentNullException.ThrowIfNull(pricingOptions);
        var options = pricingOptions.ToArray();
        var primary = RentPricingOption.PrimaryOf(options);

        return new ListingSubmissionContent(country, region, city, district, title, description, propertyType,
            ListingPurpose.Rent, primary.Amount, primary.Currency, primary.Period, options, rentalTerms);
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

    /// <summary>The rental's pricing options, or empty for a Sale and for a rental stored with a single price.</summary>
    public IReadOnlyList<RentPricingOption> PricingOptions { get; }

    /// <summary>When and how the rental can be booked, or null exactly when there are no pricing options.</summary>
    public RentalTerms? RentalTerms { get; }

    /// <summary>Whether a sale's price is open to negotiation; null when not stated, and always null for a rental.</summary>
    public bool? Negotiable { get; }

    /// <summary>The date a sale property is available from; null when not stated, and always null for a rental.</summary>
    public DateOnly? AvailableFrom { get; }
}
