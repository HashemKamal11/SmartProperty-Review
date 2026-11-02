namespace SmartProperty.Domain.Favorites;

/// <summary>
/// A property a user has marked as a favorite. One row per user and property: marking it again changes nothing.
/// </summary>
/// <remarks>
/// A link, not an aggregate with behaviour: it is created and deleted, never edited. The favorite does not own the
/// property, so archiving a property leaves the favorite in place and the user can still see and remove it.
/// </remarks>
public sealed class FavoriteProperty
{
    private FavoriteProperty()
    {
    }

    public FavoriteProperty(Guid userId, Guid propertyId, DateTimeOffset createdAt)
    {
        if (userId == Guid.Empty)
        {
            throw new ArgumentException("User id must not be empty.", nameof(userId));
        }

        if (propertyId == Guid.Empty)
        {
            throw new ArgumentException("Property id must not be empty.", nameof(propertyId));
        }

        UserId = userId;
        PropertyId = propertyId;
        CreatedAt = createdAt;
    }

    public Guid UserId { get; private set; }
    public Guid PropertyId { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
}
