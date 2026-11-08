using SmartProperty.Common.Results;
using SmartProperty.Domain.PropertyRegistry;

namespace SmartProperty.Application.PropertyRegistry.List;

internal static class GetPropertiesQueryValidator
{
    public const int MaxDistrictLength = 200;

    /// <summary>
    /// Validates every filter and hands back the normalized filter set: country trimmed and upper-cased, district
    /// trimmed, types de-duplicated.
    /// </summary>
    public static Error? Validate(GetPropertiesQuery query, out PropertyListFilter filter)
    {
        filter = new PropertyListFilter();

        string? countryCode = null;
        if (!string.IsNullOrWhiteSpace(query.CountryCode))
        {
            var trimmed = query.CountryCode.Trim();
            if (trimmed.Length != 2
                || !char.IsAsciiLetter(trimmed[0])
                || !char.IsAsciiLetter(trimmed[1]))
            {
                return GetPropertiesErrors.ValidationFailed("Country code must contain exactly two ASCII letters.");
            }

            countryCode = trimmed.ToUpperInvariant();
        }

        var types = query.Types?.Distinct().ToArray() ?? [];
        if (types.Any(type => !Enum.IsDefined(type)))
        {
            return GetPropertiesErrors.ValidationFailed("The property type is not supported.");
        }

        string? district = null;
        if (!string.IsNullOrWhiteSpace(query.District))
        {
            district = query.District.Trim();
            if (district.Length > MaxDistrictLength)
            {
                return GetPropertiesErrors.ValidationFailed($"District must not exceed {MaxDistrictLength} characters.");
            }
        }

        if (query.AreaId == Guid.Empty)
        {
            return GetPropertiesErrors.ValidationFailed("Area id must not be empty.");
        }

        if (query.MinPrice < 0m || query.MaxPrice < 0m)
        {
            return GetPropertiesErrors.ValidationFailed("Prices must not be negative.");
        }

        if (query.MinPrice is { } minPrice && query.MaxPrice is { } maxPrice && minPrice > maxPrice)
        {
            return GetPropertiesErrors.ValidationFailed("The minimum price must not exceed the maximum price.");
        }

        if (query.MinBedrooms is < 0 or > Property.MaxRoomCount || query.MaxBedrooms is < 0 or > Property.MaxRoomCount)
        {
            return GetPropertiesErrors.ValidationFailed(
                $"Bedrooms must be between 0 and {Property.MaxRoomCount}.");
        }

        if (query.MinBedrooms is { } minBedrooms && query.MaxBedrooms is { } maxBedrooms && minBedrooms > maxBedrooms)
        {
            return GetPropertiesErrors.ValidationFailed("The minimum bedrooms must not exceed the maximum bedrooms.");
        }

        if (query.MinMatchPercentage is { } minMatch)
        {
            if (minMatch is < 0m or > 100m)
            {
                return GetPropertiesErrors.ValidationFailed("Match percentage must be between 0 and 100.");
            }

            if (query.MatchUserId is null || query.MatchUserId == Guid.Empty)
            {
                return GetPropertiesErrors.ValidationFailed("Sign in to filter by match percentage.");
            }
        }

        filter = new PropertyListFilter(
            types,
            query.Status,
            countryCode,
            district,
            query.AreaId,
            query.MinPrice,
            query.MaxPrice,
            query.MinBedrooms,
            query.MaxBedrooms,
            query.MinMatchPercentage,
            query.MatchUserId is { } userId && userId != Guid.Empty ? userId : null);

        return null;
    }
}
