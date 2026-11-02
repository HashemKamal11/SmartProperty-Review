namespace SmartProperty.Domain.PropertyRegistry;

/// <summary>
/// What a property looks out on (city, garden, sea, ...). Reference data seeded by a migration; the code is the
/// AI search service's own value and is what clients send when choosing views.
/// </summary>
public sealed class PropertyView
{
    public const int MaxCodeLength = 64;
    public const int MaxNameLength = 128;

    private PropertyView()
    {
        Code = string.Empty;
        NameAr = string.Empty;
        NameEn = string.Empty;
    }

    public PropertyView(Guid id, string code, string nameAr, string nameEn, int sortOrder)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException("View id must not be empty.", nameof(id));
        }

        Id = id;
        Code = TrimRequired(code, MaxCodeLength, nameof(code));
        NameAr = TrimRequired(nameAr, MaxNameLength, nameof(nameAr));
        NameEn = TrimRequired(nameEn, MaxNameLength, nameof(nameEn));
        SortOrder = sortOrder;
    }

    public Guid Id { get; private set; }
    public string Code { get; private set; }
    public string NameAr { get; private set; }
    public string NameEn { get; private set; }
    public int SortOrder { get; private set; }

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
