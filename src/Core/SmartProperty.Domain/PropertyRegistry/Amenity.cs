namespace SmartProperty.Domain.PropertyRegistry;

/// <summary>
/// A feature or facility a property can offer (balcony, pool, gym, ...). Reference data: the set is seeded by a
/// migration and properties pick from it by <see cref="Code"/>, which is the stable identifier clients rely on.
/// </summary>
public sealed class Amenity
{
    public const int MaxCodeLength = 64;
    public const int MaxNameLength = 128;

    private Amenity()
    {
        Code = string.Empty;
        NameAr = string.Empty;
        NameEn = string.Empty;
    }

    public Amenity(Guid id, string code, string nameAr, string nameEn, int sortOrder, string? aiCode = null)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException("Amenity id must not be empty.", nameof(id));
        }

        Id = id;
        Code = TrimRequired(code, MaxCodeLength, nameof(code));
        NameAr = TrimRequired(nameAr, MaxNameLength, nameof(nameAr));
        NameEn = TrimRequired(nameEn, MaxNameLength, nameof(nameEn));
        SortOrder = sortOrder;
        AiCode = string.IsNullOrWhiteSpace(aiCode) ? null : TrimRequired(aiCode, MaxCodeLength, nameof(aiCode));
    }

    public Guid Id { get; private set; }
    public string Code { get; private set; }
    public string NameAr { get; private set; }
    public string NameEn { get; private set; }
    public int SortOrder { get; private set; }

    /// <summary>The matching code in the AI search service vocabulary, or null when it has no equivalent.</summary>
    public string? AiCode { get; private set; }

    private static string TrimRequired(string value, int maxLength, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("Value must not be empty.", parameterName);
        }

        var trimmed = value.Trim();
        if (trimmed.Length > maxLength)
        {
            throw new ArgumentException($"Value must not exceed {maxLength} characters.", parameterName);
        }

        return trimmed;
    }
}
