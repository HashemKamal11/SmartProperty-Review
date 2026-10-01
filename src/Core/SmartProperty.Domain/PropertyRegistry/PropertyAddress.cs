namespace SmartProperty.Domain.PropertyRegistry;

/// <summary>
/// Immutable location of a property. Only the ISO 3166-1 alpha-2 country code is required: land may exist
/// outside a clear municipal boundary, so the city and every remaining component are optional. Coordinates
/// are plain decimal degrees; the domain holds no spatial type and performs no geocoding.
/// </summary>
public sealed record PropertyAddress
{
    private const decimal MinimumLatitude = -90m;
    private const decimal MaximumLatitude = 90m;
    private const decimal MinimumLongitude = -180m;
    private const decimal MaximumLongitude = 180m;

    private PropertyAddress()
    {
        CountryCode = string.Empty;
    }

    public PropertyAddress(
        string countryCode,
        string? city = null,
        string? region = null,
        string? district = null,
        string? addressLine = null,
        string? postalCode = null,
        decimal? latitude = null,
        decimal? longitude = null)
    {
        CountryCode = NormalizeCountryCode(countryCode);
        City = TrimOptional(city);
        Region = TrimOptional(region);
        District = TrimOptional(district);
        AddressLine = TrimOptional(addressLine);
        PostalCode = TrimOptional(postalCode);
        Latitude = ValidateCoordinate(latitude, MinimumLatitude, MaximumLatitude, nameof(latitude));
        Longitude = ValidateCoordinate(longitude, MinimumLongitude, MaximumLongitude, nameof(longitude));
    }

    public string CountryCode { get; private set; }
    public string? City { get; private set; }
    public string? Region { get; private set; }
    public string? District { get; private set; }
    public string? AddressLine { get; private set; }
    public string? PostalCode { get; private set; }
    public decimal? Latitude { get; private set; }
    public decimal? Longitude { get; private set; }

    private static string NormalizeCountryCode(string countryCode)
    {
        var trimmed = TrimRequired(countryCode, nameof(countryCode));

        if (trimmed.Length != 2 || !char.IsAsciiLetter(trimmed[0]) || !char.IsAsciiLetter(trimmed[1]))
        {
            throw new ArgumentException(
                "Country code must be an ISO 3166-1 alpha-2 code.",
                nameof(countryCode));
        }

        return trimmed.ToUpperInvariant();
    }

    private static decimal? ValidateCoordinate(
        decimal? value,
        decimal minimum,
        decimal maximum,
        string parameterName)
    {
        if (value is null)
        {
            return null;
        }

        if (value < minimum || value > maximum)
        {
            throw new ArgumentOutOfRangeException(
                parameterName,
                value,
                $"Coordinate must be between {minimum} and {maximum} degrees.");
        }

        return value;
    }

    private static string TrimRequired(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("Value must not be empty.", parameterName);
        }

        return value.Trim();
    }

    private static string? TrimOptional(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }
}
