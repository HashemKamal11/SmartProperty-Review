namespace SmartProperty.Domain.PropertyMatching;

/// <summary>
/// One result of the external AI matching system: how well a property fits what a user searched for, with the
/// AI explanation. Append-only; a later search records new rows rather than rewriting earlier ones.
/// </summary>
public sealed class PropertyMatch
{
    public const int MaxDescriptionLength = 4000;
    public const int MaxQueryLength = 2000;

    private PropertyMatch()
    {
        Description = string.Empty;
        Query = string.Empty;
    }

    public PropertyMatch(
        Guid id,
        Guid userId,
        Guid propertyId,
        decimal matchPercentage,
        string description,
        string query,
        DateTimeOffset createdAt)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException("Property match id must not be empty.", nameof(id));
        }

        if (userId == Guid.Empty)
        {
            throw new ArgumentException("User id must not be empty.", nameof(userId));
        }

        if (propertyId == Guid.Empty)
        {
            throw new ArgumentException("Property id must not be empty.", nameof(propertyId));
        }

        if (matchPercentage is < 0m or > 100m)
        {
            throw new ArgumentOutOfRangeException(
                nameof(matchPercentage), matchPercentage, "Match percentage must be between 0 and 100.");
        }

        if (string.IsNullOrWhiteSpace(description))
        {
            throw new ArgumentException("Description must not be empty.", nameof(description));
        }

        if (string.IsNullOrWhiteSpace(query))
        {
            throw new ArgumentException("Query must not be empty.", nameof(query));
        }

        var trimmedQuery = query.Trim();
        if (trimmedQuery.Length > MaxQueryLength)
        {
            throw new ArgumentException($"Query must not exceed {MaxQueryLength} characters.", nameof(query));
        }

        var trimmed = description.Trim();
        if (trimmed.Length > MaxDescriptionLength)
        {
            throw new ArgumentException(
                $"Description must not exceed {MaxDescriptionLength} characters.", nameof(description));
        }

        Id = id;
        UserId = userId;
        PropertyId = propertyId;
        MatchPercentage = matchPercentage;
        Description = trimmed;
        Query = trimmedQuery;
        CreatedAt = createdAt;
    }

    public Guid Id { get; private set; }
    public Guid UserId { get; private set; }
    public Guid PropertyId { get; private set; }
    public decimal MatchPercentage { get; private set; }
    public string Description { get; private set; }
    public string Query { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
}
