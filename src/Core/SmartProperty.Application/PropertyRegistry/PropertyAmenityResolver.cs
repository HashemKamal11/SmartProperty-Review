using SmartProperty.Application.Abstractions.Persistence.Repositories;
using SmartProperty.Common.Results;
using SmartProperty.Domain.PropertyRegistry;

namespace SmartProperty.Application.PropertyRegistry;

/// <summary>
/// Turns the amenity codes a client chose into tracked amenities, shared by create and update so both accept and
/// refuse exactly the same input.
/// </summary>
internal static class PropertyAmenityResolver
{
    /// <summary>
    /// A null or empty list is valid and means "no amenities". Duplicate codes collapse to one. A blank code or a
    /// code that names no amenity is a validation failure carrying <paramref name="validationFailed"/>.
    /// </summary>
    public static async Task<Result<IReadOnlyList<Amenity>>> ResolveAsync(
        IAmenityRepository amenityRepository,
        IReadOnlyList<string>? codes,
        Func<string, Error> validationFailed,
        CancellationToken cancellationToken)
    {
        if (codes is null || codes.Count == 0)
        {
            return Result<IReadOnlyList<Amenity>>.Success([]);
        }

        if (codes.Any(string.IsNullOrWhiteSpace))
        {
            return Result<IReadOnlyList<Amenity>>.Failure(validationFailed("Amenity codes must not be blank."));
        }

        var distinct = codes.Select(code => code.Trim()).Distinct(StringComparer.Ordinal).ToArray();
        var found = await amenityRepository.GetByCodesAsync(distinct, cancellationToken);

        var unknown = distinct.Except(found.Select(amenity => amenity.Code), StringComparer.Ordinal).ToArray();
        if (unknown.Length > 0)
        {
            return Result<IReadOnlyList<Amenity>>.Failure(validationFailed(
                $"Unknown amenity code(s): {string.Join(", ", unknown)}."));
        }

        return Result<IReadOnlyList<Amenity>>.Success(found);
    }
}
