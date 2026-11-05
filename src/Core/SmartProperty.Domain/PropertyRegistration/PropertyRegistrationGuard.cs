namespace SmartProperty.Domain.PropertyRegistration;

/// <summary>
/// Shared argument checks for the registration types, so every entity rejects bad input the same way.
/// </summary>
internal static class PropertyRegistrationGuard
{
    public static Guid RequireId(Guid id, string parameterName)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException("Id must not be empty.", parameterName);
        }

        return id;
    }

    public static string RequireText(string? value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("Value must not be empty.", parameterName);
        }

        return value.Trim();
    }
}
