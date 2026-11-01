using SmartProperty.Application.PropertyRegistry.Create;
using SmartProperty.Domain.PropertyRegistry;
using SmartProperty.UnitTests.TestDoubles;
using Xunit;

namespace SmartProperty.UnitTests.Application.PropertyRegistry;

public sealed class PropertySpecificationsCommandTests
{
    private readonly FakePropertyRepository _properties = new();

    [Fact]
    public async Task CreateStoresBedroomsBathroomsBuiltDateAndFinishingStatus()
    {
        var result = await CreateHandler().Handle(new CreatePropertyCommand(
            "Unit",
            "EG",
            Bedrooms: 3,
            Bathrooms: 2,
            BuiltDate: new DateOnly(2020, 5, 1),
            FinishingStatus: "SuperLux"));

        Assert.True(result.IsSuccess);
        var property = Assert.Single(_properties.Added);
        Assert.Equal(3, property.Bedrooms);
        Assert.Equal(2, property.Bathrooms);
        Assert.Equal(new DateOnly(2020, 5, 1), property.BuiltDate);
        Assert.Equal(FinishingStatus.SuperLux, property.FinishingStatus);
        Assert.Equal(FinishingStatus.SuperLux, result.Value.FinishingStatus);
    }

    [Fact]
    public async Task CreateWithoutSpecificationsLeavesThemNull()
    {
        var result = await CreateHandler().Handle(new CreatePropertyCommand("Land", "EG"));

        Assert.True(result.IsSuccess);
        var property = Assert.Single(_properties.Added);
        Assert.Null(property.Bedrooms);
        Assert.Null(property.Bathrooms);
        Assert.Null(property.BuiltDate);
        Assert.Null(property.FinishingStatus);
    }

    [Theory]
    [InlineData(-1, null, null, null)]
    [InlineData(101, null, null, null)]
    [InlineData(null, -1, null, null)]
    [InlineData(null, 101, null, null)]
    [InlineData(null, null, "1799-12-31", null)]
    [InlineData(null, null, null, "unfinished")]
    [InlineData(null, null, null, "Other")]
    public async Task InvalidSpecificationsAreRejected(
        int? bedrooms,
        int? bathrooms,
        string? builtDate,
        string? finishingStatus)
    {
        var result = await CreateHandler().Handle(new CreatePropertyCommand(
            "Unit",
            "EG",
            Bedrooms: bedrooms,
            Bathrooms: bathrooms,
            BuiltDate: builtDate is null ? null : DateOnly.Parse(builtDate),
            FinishingStatus: finishingStatus));

        Assert.True(result.IsFailure);
        Assert.Equal("validation.failed", result.Error!.Code);
        Assert.Empty(_properties.Added);
    }

    private CreatePropertyCommandHandler CreateHandler()
    {
        return new CreatePropertyCommandHandler(
            _properties,
            new FakeDateTimeProvider(new DateTimeOffset(2026, 11, 1, 9, 0, 0, TimeSpan.Zero)),
            new FakeUnitOfWork());
    }
}
