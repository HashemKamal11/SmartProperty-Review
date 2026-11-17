using SmartProperty.Common.Results;
using SmartProperty.Domain.PropertyRegistry;

namespace SmartProperty.Application.PropertyRegistry.List;

internal static class GetPropertiesQueryValidator
{
    public const int MaxDistrictLength = 200;
    public const int MaxSearchLength = 100;
    public const int MaxSearchTerms = 5;

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

        if (query.Sort is { } sort && !Enum.IsDefined(sort))
        {
            return GetPropertiesErrors.ValidationFailed("The sort order is not supported.");
        }

        string[] searchTerms = [];
        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var search = query.Search.Trim();
            if (search.Length > MaxSearchLength)
            {
                return GetPropertiesErrors.ValidationFailed($"Search text must not exceed {MaxSearchLength} characters.");
            }

            searchTerms = search
                .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            if (searchTerms.Length > MaxSearchTerms)
            {
                return GetPropertiesErrors.ValidationFailed($"Search text must not exceed {MaxSearchTerms} words.");
            }
        }

        if (query.AreaId == Guid.Empty)
        {
            return GetPropertiesErrors.ValidationFailed("Area id must not be empty.");
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

        if (query.CityId == Guid.Empty)
        {
            return GetPropertiesErrors.ValidationFailed("City id must not be empty.");
        }

        if (query.CompoundId == Guid.Empty)
        {
            return GetPropertiesErrors.ValidationFailed("Compound id must not be empty.");
        }

        if (query.MinBathrooms is < 0 or > Property.MaxRoomCount || query.MaxBathrooms is < 0 or > Property.MaxRoomCount)
        {
            return GetPropertiesErrors.ValidationFailed(
                $"Bathrooms must be between 0 and {Property.MaxRoomCount}.");
        }

        if (query.MinBathrooms is { } minBathrooms && query.MaxBathrooms is { } maxBathrooms && minBathrooms > maxBathrooms)
        {
            return GetPropertiesErrors.ValidationFailed("The minimum bathrooms must not exceed the maximum bathrooms.");
        }

        var finishingStatuses = query.FinishingStatuses?.Distinct().ToArray() ?? [];
        if (finishingStatuses.Any(status => !Enum.IsDefined(status)))
        {
            return GetPropertiesErrors.ValidationFailed("The finishing status is not supported.");
        }

        if (query.MinAreaSqm is < 0m or > PropertyPhysicalInfo.MaxAreaSqm
            || query.MaxAreaSqm is < 0m or > PropertyPhysicalInfo.MaxAreaSqm)
        {
            return GetPropertiesErrors.ValidationFailed(
                $"Area must be between 0 and {PropertyPhysicalInfo.MaxAreaSqm} square metres.");
        }

        if (query.MinAreaSqm is { } minArea && query.MaxAreaSqm is { } maxArea && minArea > maxArea)
        {
            return GetPropertiesErrors.ValidationFailed("The minimum area must not exceed the maximum area.");
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
            // No status means every status: a reader sees their Active and Archived properties together.
            query.Status,
            countryCode,
            district,
            query.AreaId,
            query.MinBedrooms,
            query.MaxBedrooms,
            query.MinMatchPercentage,
            query.MatchUserId is { } userId && userId != Guid.Empty ? userId : null,
            searchTerms,
            query.Sort ?? PropertyListSortOrder.Relevance,
            query.OwnerUserId is { } owner && owner != Guid.Empty ? owner : null,
            query.CityId,
            query.CompoundId,
            query.MinBathrooms,
            query.MaxBathrooms,
            query.Furnished,
            finishingStatuses,
            query.MinAreaSqm,
            query.MaxAreaSqm);

        return null;
    }
}
