namespace SmartProperty.Domain.Identity;

public sealed class Permission
{
    private Permission()
    {
        Code = string.Empty;
    }

    public Permission(Guid id, string code, string? description = null)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException("Permission id must not be empty.", nameof(id));
        }

        Id = id;
        Code = TrimRequired(code, nameof(code));
        Description = TrimOptional(description);
    }

    public Guid Id { get; private set; }
    public string Code { get; private set; }
    public string? Description { get; private set; }

    public void UpdateDescription(string? description)
    {
        Description = TrimOptional(description);
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
