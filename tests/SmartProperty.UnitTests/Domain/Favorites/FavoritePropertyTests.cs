using SmartProperty.Domain.Favorites;
using Xunit;

namespace SmartProperty.UnitTests.Domain.Favorites;

public sealed class FavoritePropertyTests
{
    private static readonly DateTimeOffset At = new(2026, 11, 3, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Constructor_RetainsEveryValue()
    {
        var userId = Guid.NewGuid();
        var propertyId = Guid.NewGuid();

        var favorite = new FavoriteProperty(userId, propertyId, At);

        Assert.Equal(userId, favorite.UserId);
        Assert.Equal(propertyId, favorite.PropertyId);
        Assert.Equal(At, favorite.CreatedAt);
    }

    [Fact]
    public void Constructor_RejectsAnEmptyUserId()
    {
        Assert.Throws<ArgumentException>(() => new FavoriteProperty(Guid.Empty, Guid.NewGuid(), At));
    }

    [Fact]
    public void Constructor_RejectsAnEmptyPropertyId()
    {
        Assert.Throws<ArgumentException>(() => new FavoriteProperty(Guid.NewGuid(), Guid.Empty, At));
    }
}
