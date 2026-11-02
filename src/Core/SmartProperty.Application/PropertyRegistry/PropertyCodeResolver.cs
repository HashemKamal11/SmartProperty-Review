using SmartProperty.Common.Results;

namespace SmartProperty.Application.PropertyRegistry;

/// <summary>
/// Turns the codes a client chose from a reference list (amenities, views) into the tracked reference rows, shared
/// by create and update so both accept and refuse exactly the same input.
/// </summary>
internal static class PropertyCodeResolver
{
    /// <summary>
    /// A null or empty list is valid and means "none". Duplicate codes collapse to one. A blank code, or a code
    /// that names nothing, is a validation failure carrying <paramref name="validationFailed"/>.
    /// </summary>
    /// <param name="noun">What the codes are, for the message: "Amenity" or "View".</param>
    public static async Task<Result<IReadOnlyList<T>>> ResolveAsync<T>(
        string noun,
        IReadOnlyList<string>? codes,
        Func<IReadOnlyCollection<string>, CancellationToken, Task<IReadOnlyList<T>>> fetch,
        Func<T, string> codeOf,
        Func<string, Error> validationFailed,
        CancellationToken cancellationToken)
    {
        if (codes is null || codes.Count == 0)
        {
            return Result<IReadOnlyList<T>>.Success([]);
        }

        if (codes.Any(string.IsNullOrWhiteSpace))
        {
            return Result<IReadOnlyList<T>>.Failure(validationFailed($"{noun} codes must not be blank."));
        }

        var distinct = codes.Select(code => code.Trim()).Distinct(StringComparer.Ordinal).ToArray();
        var found = await fetch(distinct, cancellationToken);

        var unknown = distinct.Except(found.Select(codeOf), StringComparer.Ordinal).ToArray();
        if (unknown.Length > 0)
        {
            return Result<IReadOnlyList<T>>.Failure(validationFailed(
                $"Unknown {noun.ToLowerInvariant()} code(s): {string.Join(", ", unknown)}."));
        }

        return Result<IReadOnlyList<T>>.Success(found);
    }
}
