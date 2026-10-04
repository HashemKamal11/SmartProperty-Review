using SmartProperty.Application.PropertyRegistry.Create;
using SmartProperty.Common.Results;
using SmartProperty.Domain.PropertyRegistry;
using SmartProperty.UnitTests.TestDoubles;
using Xunit;

namespace SmartProperty.UnitTests.Application.PropertyRegistry;

public sealed class CreatePropertyCommandHandlerTests
{
    private static readonly DateTimeOffset CreatedAt =
        new(2026, 10, 4, 8, 30, 0, TimeSpan.Zero);

    private readonly FakePropertyRepository _properties = new();
    private readonly FakeDateTimeProvider _clock = new(CreatedAt);
    private readonly FakeUnitOfWork _unitOfWork = new();

    [Fact]
    public async Task ValidCommandCreatesAndReturnsTheCanonicalProperty()
    {
        var handler = CreateHandler();

        var result = await handler.Handle(new CreatePropertyCommand(
            "Building",
            " sa ",
            " Riyadh ",
            " Riyadh Province ",
            " Al Olaya ",
            " King Fahd Road ",
            " 12211 ",
            24.7136m,
            46.6753m));

        Assert.True(result.IsSuccess);
        var property = Assert.Single(_properties.Added);
        Assert.NotEqual(Guid.Empty, property.Id);
        Assert.Equal(PropertyType.Building, property.Type);
        Assert.Equal(PropertyStatus.Active, property.Status);
        Assert.Equal("SA", property.Address.CountryCode);
        Assert.Equal("Riyadh", property.Address.City);
        Assert.Equal("Riyadh Province", property.Address.Region);
        Assert.Equal("Al Olaya", property.Address.District);
        Assert.Equal("King Fahd Road", property.Address.AddressLine);
        Assert.Equal("12211", property.Address.PostalCode);
        Assert.Equal(24.7136m, property.Address.Latitude);
        Assert.Equal(46.6753m, property.Address.Longitude);
        Assert.Equal(CreatedAt, property.CreatedAt);
        Assert.Equal(CreatedAt, property.UpdatedAt);
        Assert.Equal(1, _clock.UtcNowCallCount);
        Assert.Equal(1, _properties.AddCallCount);
        Assert.Equal(1, _unitOfWork.SaveChangesCallCount);

        Assert.Equal(property.Id, result.Value.Id);
        Assert.Equal(property.Type, result.Value.Type);
        Assert.Equal(property.Status, result.Value.Status);
        Assert.Equal(property.Address.CountryCode, result.Value.Address.CountryCode);
        Assert.Equal(property.CreatedAt, result.Value.CreatedAt);
        Assert.Equal(property.UpdatedAt, result.Value.UpdatedAt);
    }

    [Fact]
    public async Task MinimumCommandKeepsEveryOptionalAddressFieldNull()
    {
        var result = await CreateHandler().Handle(new CreatePropertyCommand("Land", "SA"));

        Assert.True(result.IsSuccess);
        Assert.Null(result.Value.Address.City);
        Assert.Null(result.Value.Address.Region);
        Assert.Null(result.Value.Address.District);
        Assert.Null(result.Value.Address.AddressLine);
        Assert.Null(result.Value.Address.PostalCode);
        Assert.Null(result.Value.Address.Latitude);
        Assert.Null(result.Value.Address.Longitude);
    }

    [Theory]
    [InlineData(null, "SA")]
    [InlineData("", "SA")]
    [InlineData("House", "SA")]
    [InlineData("1", "SA")]
    [InlineData("Land", null)]
    [InlineData("Land", "")]
    [InlineData("Land", "S")]
    [InlineData("Land", "SAU")]
    [InlineData("Land", "S1")]
    public async Task InvalidRequiredValuesReturnValidationWithoutWriting(string? type, string? countryCode)
    {
        var result = await CreateHandler().Handle(new CreatePropertyCommand(type, countryCode));

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorType.Validation, result.Error!.Type);
        Assert.Empty(_properties.Added);
        Assert.Equal(0, _clock.UtcNowCallCount);
        Assert.Equal(0, _unitOfWork.SaveChangesCallCount);
    }

    [Theory]
    [InlineData("-90", null)]
    [InlineData("90", null)]
    [InlineData(null, "-180")]
    [InlineData(null, "180")]
    public async Task BoundaryAndIndependentCoordinatesAreAccepted(string? latitude, string? longitude)
    {
        var result = await CreateHandler().Handle(new CreatePropertyCommand(
            "Unit",
            "SA",
            Latitude: Parse(latitude),
            Longitude: Parse(longitude)));

        Assert.True(result.IsSuccess);
        Assert.Equal(Parse(latitude), result.Value.Address.Latitude);
        Assert.Equal(Parse(longitude), result.Value.Address.Longitude);
    }

    [Theory]
    [InlineData("-90.1", null)]
    [InlineData("90.1", null)]
    [InlineData(null, "-180.1")]
    [InlineData(null, "180.1")]
    public async Task OutOfRangeCoordinatesReturnValidation(string? latitude, string? longitude)
    {
        var result = await CreateHandler().Handle(new CreatePropertyCommand(
            "Land",
            "SA",
            Latitude: Parse(latitude),
            Longitude: Parse(longitude)));

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorType.Validation, result.Error!.Type);
        Assert.Empty(_properties.Added);
        Assert.Equal(0, _unitOfWork.SaveChangesCallCount);
    }

    private CreatePropertyCommandHandler CreateHandler()
    {
        return new CreatePropertyCommandHandler(_properties, _clock, _unitOfWork);
    }

    private static decimal? Parse(string? value)
    {
        return value is null
            ? null
            : decimal.Parse(value, System.Globalization.CultureInfo.InvariantCulture);
    }
}
