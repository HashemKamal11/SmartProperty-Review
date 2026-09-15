namespace SmartProperty.Domain.Workspaces;

public sealed class Workspace
{
    private Workspace()
    {
        Name = string.Empty;
    }

    public Workspace(Guid id, string name, DateTimeOffset createdAt)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException("Workspace id must not be empty.", nameof(id));
        }

        Id = id;
        Name = TrimRequired(name, nameof(name));
        CreatedAt = createdAt;
        UpdatedAt = createdAt;
    }

    public Guid Id { get; private set; }
    public string Name { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }

    public void Rename(string name, DateTimeOffset updatedAt)
    {
        ValidateUpdatedAt(updatedAt);

        Name = TrimRequired(name, nameof(name));
        UpdatedAt = updatedAt;
    }

    private void ValidateUpdatedAt(DateTimeOffset updatedAt)
    {
        if (updatedAt < UpdatedAt)
        {
            throw new ArgumentException(
                "Updated date cannot be earlier than the current updated date.",
                nameof(updatedAt));
        }
    }

    private static string TrimRequired(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("Value must not be empty.", parameterName);
        }

        return value.Trim();
    }
}
