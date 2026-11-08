namespace SmartProperty.Domain.PropertyComparison;

/// <summary>
/// A property a user has put on their comparison list. A list holds at most <see cref="MaxItems"/> properties, each in
/// its own position, and one property appears in a list once.
/// </summary>
/// <remarks>
/// A link, like a favorite: it is created and deleted, never edited. The position is what lets the database itself refuse
/// a fourth property when two requests race, since two items cannot share a position and there are only three.
/// </remarks>
public sealed class PropertyComparisonItem
{
    public const int MaxItems = 3;
    public const int MinItemsToCompare = 2;

    private PropertyComparisonItem()
    {
    }

    public PropertyComparisonItem(Guid userId, Guid propertyId, int position, DateTimeOffset addedAt)
    {
        if (userId == Guid.Empty)
        {
            throw new ArgumentException("User id must not be empty.", nameof(userId));
        }

        if (propertyId == Guid.Empty)
        {
            throw new ArgumentException("Property id must not be empty.", nameof(propertyId));
        }

        if (position is < 1 or > MaxItems)
        {
            throw new ArgumentOutOfRangeException(
                nameof(position), position, $"Position must be between 1 and {MaxItems}.");
        }

        UserId = userId;
        PropertyId = propertyId;
        Position = position;
        AddedAt = addedAt;
    }

    public Guid UserId { get; private set; }
    public Guid PropertyId { get; private set; }
    public int Position { get; private set; }
    public DateTimeOffset AddedAt { get; private set; }
}
