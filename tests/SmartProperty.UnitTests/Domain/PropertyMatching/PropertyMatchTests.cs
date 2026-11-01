using SmartProperty.Domain.PropertyMatching;
using Xunit;

namespace SmartProperty.UnitTests.Domain.PropertyMatching;

public sealed class PropertyMatchTests
{
    private static readonly DateTimeOffset CreatedAt = new(2026, 11, 1, 9, 0, 0, TimeSpan.Zero);
    private static readonly Guid UserId = Guid.NewGuid();
    private static readonly Guid PropertyId = Guid.NewGuid();

    [Fact]
    public void Constructor_RetainsEveryValueAndTrimsText()
    {
        var id = Guid.NewGuid();

        var match = new PropertyMatch(id, UserId, PropertyId, 92.5m, "  Great fit.  ", "  3 bed near metro  ", CreatedAt);

        Assert.Equal(id, match.Id);
        Assert.Equal(UserId, match.UserId);
        Assert.Equal(PropertyId, match.PropertyId);
        Assert.Equal(92.5m, match.MatchPercentage);
        Assert.Equal("Great fit.", match.Description);
        Assert.Equal("3 bed near metro", match.Query);
        Assert.Equal(CreatedAt, match.CreatedAt);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(100)]
    public void Constructor_AcceptsTheBoundaryPercentages(int percentage)
    {
        var match = Create(percentage: percentage);

        Assert.Equal(percentage, match.MatchPercentage);
    }

    [Theory]
    [InlineData(-0.01)]
    [InlineData(100.01)]
    public void Constructor_RejectsAnOutOfRangePercentage(double percentage)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Create(percentage: (decimal)percentage));
    }

    [Fact]
    public void Constructor_RejectsAnEmptyId()
    {
        Assert.Throws<ArgumentException>(() => Create(id: Guid.Empty));
    }

    [Fact]
    public void Constructor_RejectsAnEmptyUserId()
    {
        Assert.Throws<ArgumentException>(() => Create(userId: Guid.Empty));
    }

    [Fact]
    public void Constructor_RejectsAnEmptyPropertyId()
    {
        Assert.Throws<ArgumentException>(() => Create(propertyId: Guid.Empty));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Constructor_RejectsABlankDescription(string description)
    {
        Assert.Throws<ArgumentException>(() => Create(description: description));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Constructor_RejectsABlankQuery(string query)
    {
        Assert.Throws<ArgumentException>(() => Create(query: query));
    }

    [Fact]
    public void Constructor_AcceptsTextAtTheMaximumLengthAndRejectsOneMore()
    {
        Create(description: new string('d', PropertyMatch.MaxDescriptionLength));
        Create(query: new string('q', PropertyMatch.MaxQueryLength));

        Assert.Throws<ArgumentException>(() =>
            Create(description: new string('d', PropertyMatch.MaxDescriptionLength + 1)));
        Assert.Throws<ArgumentException>(() =>
            Create(query: new string('q', PropertyMatch.MaxQueryLength + 1)));
    }

    private static PropertyMatch Create(
        Guid? id = null,
        Guid? userId = null,
        Guid? propertyId = null,
        decimal percentage = 50m,
        string description = "Good fit.",
        string query = "flat in cairo")
    {
        return new PropertyMatch(
            id ?? Guid.NewGuid(),
            userId ?? UserId,
            propertyId ?? PropertyId,
            percentage,
            description,
            query,
            CreatedAt);
    }
}
