namespace SmartProperty.Domain.Locations;

internal static class LocationRules
{
    public const int MaxNameLength = 200;

    public static T NotNull<T>(T? value)
        where T : class
    {
        return value ?? throw new ArgumentNullException(nameof(value));
    }

    public static Guid RequireId(Guid id)
    {
        return id == Guid.Empty ? throw new ArgumentException("Location id must not be empty.", nameof(id)) : id;
    }

    public static int RequireAiId(int aiId)
    {
        return aiId > 0
            ? aiId
            : throw new ArgumentOutOfRangeException(nameof(aiId), aiId, "The AI service id must be positive.");
    }

    public static Guid RequireParentId(Guid parentId, string parameterName)
    {
        return parentId == Guid.Empty
            ? throw new ArgumentException("Parent location id must not be empty.", parameterName)
            : parentId;
    }

    public static string RequireName(string name, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("Name must not be empty.", parameterName);
        }

        var trimmed = name.Trim();

        return trimmed.Length <= MaxNameLength
            ? trimmed
            : throw new ArgumentException($"Name must not exceed {MaxNameLength} characters.", parameterName);
    }
}
